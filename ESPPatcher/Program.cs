using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastRsync.Delta;
using FastRsync.Diagnostics;
using ICSharpCode.SharpZipLib.Zip;

namespace ESPPatcher;

internal static class Program
{
    // Mutex name remains unchanged
    private static readonly string MutexName = "TarkovUpdaterMutex";

    private static Mutex? _mutex;
    private static bool _mutexAcquired; // Flag to track mutex acquisition

    static async Task<int> Main()
    {
        try
        {
            // 0. Get paths from the user
            string gameRootDir = GetGameDirectoryFromUser();
            string updatePatch = GetUpdatePatchFromUser();

            // Initialize other paths based on user input
            string oldFileRoot = Path.Combine(gameRootDir, "LastUpdateFile");
            string patchedDataFile = Path.Combine(gameRootDir, "LastRunHistory.json");
            string gameExePath = Path.Combine(gameRootDir, "EscapeFromTarkov.exe");

            // 1. Check for running process using a mutex
            if (!AcquireUpdateMutex())
            {
                Console.WriteLine(
                    "Another update process is already running. Please wait for it to complete or terminate it manually.");
                return 1;
            }

            // 2. Check if the update package exists
            if (!File.Exists(updatePatch))
            {
                Console.WriteLine($"Update patch file not found: {updatePatch}");
                return 1;
            }

            // 3. Read update metadata
            UpdateMetadata updateData;
            try
            {
                updateData = await ReadUpdateMetadata(updatePatch);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read the update package: {ex.Message}");
                return 1;
            }

            // 4. Validate game version
            if (!ValidateGameVersion(updateData.FromVersion, gameExePath))
            {
                Console.WriteLine($"Game version mismatch. Expected version: {updateData.FromVersion}");
                Console.WriteLine("It is recommended to verify the game's integrity and repair it before retrying.");
                return 1;
            }

            // 5. Handle run history and resume functionality
            var runHistory = LoadOrCreateRunHistory(updateData, patchedDataFile);

            if (runHistory.IsCompleted)
            {
                // Clean up backup files from the last completed run
                CleanupOldFileRoot(oldFileRoot);
                runHistory = CreateNewRunHistory(updateData);
            }
            else if (runHistory.FromVersion != updateData.FromVersion || runHistory.ToVersion != updateData.ToVersion)
            {
                // Version mismatch detected for an incomplete update
                Console.WriteLine("Incomplete update with a version mismatch detected.");
                Console.WriteLine("Select an option:");
                Console.WriteLine("1. Perform a full rollback to the original state");
                Console.WriteLine("2. Delete history and start over");
                Console.WriteLine("3. Exit");

                var choice = Console.ReadLine();
                switch (choice)
                {
                    case "1":
                        if (ExecuteGlobalRollback(oldFileRoot, gameRootDir, patchedDataFile))
                        {
                            Console.WriteLine("Full rollback completed successfully.");
                            return 0;
                        }
                        else
                        {
                            Console.WriteLine("Full rollback failed.");
                            return 1;
                        }
                    case "2":
                        File.Delete(patchedDataFile);
                        CleanupOldFileRoot(oldFileRoot);
                        runHistory = CreateNewRunHistory(updateData);
                        break;
                    default:
                        return 0;
                }
            }

            // 6. Ensure the backup directory exists
            Directory.CreateDirectory(oldFileRoot);

            // 7. Execute the update process
            var success = await ExecuteUpdate(updateData, runHistory, updatePatch, gameRootDir, oldFileRoot,
                patchedDataFile);

            if (success)
            {
                runHistory.IsCompleted = true;
                runHistory.CompletedAt = DateTime.Now;
                SaveRunHistory(runHistory, patchedDataFile);
                Console.WriteLine("Update completed successfully!");
                return 0;
            }
            else
            {
                Console.WriteLine("Update failed. Select an option:");
                Console.WriteLine("1. Perform a full rollback");
                Console.WriteLine("2. Exit");

                var choice = Console.ReadLine();
                if (choice == "1")
                {
                    ExecuteGlobalRollback(oldFileRoot, gameRootDir, patchedDataFile);
                }

                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An unexpected error occurred during execution: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            // Only release the mutex if it was successfully acquired
            if (_mutexAcquired)
            {
                _mutex?.ReleaseMutex();
            }

            _mutex?.Dispose();
        }
    }

    private static string GetGameDirectoryFromUser()
    {
        while (true)
        {
            Console.WriteLine(
                "\nPlease enter the game's root directory path, or drag and drop the game's executable/folder into the window and press Enter:");
            var gamePathInput = Console.ReadLine()?.Trim('"', ' ');

            if (string.IsNullOrWhiteSpace(gamePathInput))
            {
                Console.WriteLine("The path cannot be empty. Please try again.");
                continue;
            }

            string? directoryPath = null;
            if (File.Exists(gamePathInput))
            {
                directoryPath = Path.GetDirectoryName(gamePathInput);
            }
            else if (Directory.Exists(gamePathInput))
            {
                directoryPath = gamePathInput;
            }

            if (directoryPath != null && Directory.Exists(directoryPath))
            {
                Console.WriteLine($"Game root directory confirmed: {directoryPath}");
                return directoryPath;
            }

            Console.WriteLine("The entered path is invalid or does not exist. Please check and try again.");
        }
    }

    private static string GetUpdatePatchFromUser()
    {
        while (true)
        {
            Console.WriteLine(
                "\nPlease enter the full path to the update package file (.update), or drag and drop the file into the window and press Enter:");
            var patchPathInput = Console.ReadLine()?.Trim('"', ' ');

            if (string.IsNullOrWhiteSpace(patchPathInput))
            {
                Console.WriteLine("The path cannot be empty. Please try again.");
                continue;
            }

            if (File.Exists(patchPathInput))
            {
                Console.WriteLine($"Update package file confirmed: {patchPathInput}");
                return patchPathInput;
            }

            Console.WriteLine("The file does not exist or the path is invalid. Please check and try again.");
        }
    }

    private static bool AcquireUpdateMutex()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _mutexAcquired = createdNew; // Set flag based on whether a new mutex was created
            return createdNew;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<UpdateMetadata> ReadUpdateMetadata(string updatePatch)
    {
        await using var fileStream = File.OpenRead(updatePatch);
        using var zipFile = new ZipFile(fileStream);
        zipFile.IsStreamOwner = false;

        var updateInfoEntry = zipFile.Cast<ZipEntry>().FirstOrDefault(entry => entry.Name == "UpdateInfo");
        if (updateInfoEntry == null)
        {
            throw new InvalidOperationException("Could not find 'UpdateInfo' file in the update package.");
        }

        using var memoryStream = new MemoryStream();
        await using (var entryStream = zipFile.GetInputStream(updateInfoEntry))
        {
            await entryStream.CopyToAsync(memoryStream);
        }

        memoryStream.Position = 0;
        using var streamReader = new StreamReader(memoryStream);
        var jsonContent = await streamReader.ReadToEndAsync();

        var metadata = JsonSerializer.Deserialize<UpdateMetadata>(jsonContent, JsonContext.Default.UpdateMetadata);
        return metadata ?? throw new InvalidOperationException("Failed to parse update metadata.");
    }

    private static bool ValidateGameVersion(string expectedVersion, string gameExePath)
    {
        if (!File.Exists(gameExePath))
        {
            Console.WriteLine("Game executable not found. Cannot validate version.");
            return false;
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(gameExePath);
            var fileVersion = versionInfo.FileVersion ?? "Unknown";
            var productVersion = versionInfo.ProductVersion ?? "Unknown";

            Console.WriteLine($"Current FileVersion: {fileVersion}");
            Console.WriteLine($"Current ProductVersion: {productVersion}");
            Console.WriteLine($"Expected Version: {expectedVersion}");

            // Strategy 1: Prioritize matching the processed ProductVersion
            var normalizedProductVersion =
                productVersion.Split('-')[0] + "." + productVersion.Split('-').ElementAtOrDefault(1);
            if (normalizedProductVersion == expectedVersion)
            {
                Console.WriteLine("Version validation successful (based on ProductVersion).");
                return true;
            }

            // Strategy 2: Try a direct match with FileVersion
            if (fileVersion == expectedVersion)
            {
                Console.WriteLine("Version validation successful (based on FileVersion).");
                return true;
            }

            // Strategy 3: Advanced parsing of FileVersion to match cases like "0.16.8.37972" vs "0.16.8.0.37972"
            var fileVersionParts = new Version(fileVersion);
            var expectedVersionParts = new Version(expectedVersion);

            if (fileVersionParts.Major == expectedVersionParts.Major &&
                fileVersionParts.Minor == expectedVersionParts.Minor &&
                fileVersionParts.Build == expectedVersionParts.Revision)
            {
                Console.WriteLine("Version validation successful (based on parsed version comparison).");
                return true;
            }

            // If all attempts fail
            Console.WriteLine("Version validation failed. The current version does not match the expected version.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Version validation failed: {ex.Message}");
            return false;
        }
    }

    private static RunHistory LoadOrCreateRunHistory(UpdateMetadata updateData, string patchedDataFile)
    {
        if (!File.Exists(patchedDataFile))
        {
            return CreateNewRunHistory(updateData);
        }

        try
        {
            var json = File.ReadAllText(patchedDataFile);
            return JsonSerializer.Deserialize<RunHistory>(json, JsonContext.Default.RunHistory) ??
                   CreateNewRunHistory(updateData);
        }
        catch
        {
            return CreateNewRunHistory(updateData);
        }
    }

    private static RunHistory CreateNewRunHistory(UpdateMetadata updateData)
    {
        return new RunHistory
        {
            FromVersion = updateData.FromVersion,
            ToVersion = updateData.ToVersion,
            StartedAt = DateTime.Now,
            CurrentIndex = 0,
            IsCompleted = false
        };
    }

    private static void SaveRunHistory(RunHistory history, string patchedDataFile)
    {
        var json = JsonSerializer.Serialize(history, JsonContext.Default.RunHistory);
        File.WriteAllText(patchedDataFile, json);
    }

    private static void CleanupOldFileRoot(string oldFileRoot)
    {
        if (!Directory.Exists(oldFileRoot)) return;
        try
        {
            Directory.Delete(oldFileRoot, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while cleaning up old backup files: {ex.Message}");
        }
    }

    private static async Task<bool> ExecuteUpdate(UpdateMetadata updateData, RunHistory runHistory, string updatePatch,
        string gameRootDir, string oldFileRoot, string patchedDataFile)
    {
        await using var fileStream = File.OpenRead(updatePatch);
        using var zipFile = new ZipFile(fileStream);
        zipFile.IsStreamOwner = false;

        var totalFiles = updateData.Files.Count;

        for (var i = runHistory.CurrentIndex; i < totalFiles; i++)
        {
            var file = updateData.Files[i];
            Console.WriteLine($"Processing file [{i + 1}/{totalFiles}]: {file.Path} ({file.State})");

            try
            {
                var success = await ProcessSingleFile(file, zipFile, gameRootDir, oldFileRoot);
                if (!success)
                {
                    runHistory.CurrentIndex = i;
                    runHistory.LastError = $"Failed to process file {file.Path}";
                    SaveRunHistory(runHistory, patchedDataFile);
                    return false;
                }

                runHistory.CurrentIndex = i + 1;
                runHistory.LastError = null;
                SaveRunHistory(runHistory, patchedDataFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An exception occurred while processing file {file.Path}: {ex.Message}");
                runHistory.CurrentIndex = i;
                runHistory.LastError = ex.Message;
                SaveRunHistory(runHistory, patchedDataFile);
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> ProcessSingleFile(UpdateFileEntry file, ZipFile zipFile, string gameRootDir,
        string oldFileRoot)
    {
        var targetPath = Path.Combine(gameRootDir, file.Path);
        var backupPath = Path.Combine(oldFileRoot, file.Path);

        try
        {
            switch (file.State)
            {
                case UpdateFileEntryState.Modified:
                    return await ProcessModifiedFile(file, targetPath, backupPath, zipFile);

                case UpdateFileEntryState.New:
                    return await ProcessNewFile(file, targetPath, backupPath, zipFile);

                case UpdateFileEntryState.Deleted:
                    return ProcessDeletedFile(targetPath, backupPath);

                default:
                    Console.WriteLine($"Unknown file state: {file.State}");
                    return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to process file: {ex.Message}");
            RestoreFileFromBackup(targetPath, backupPath);
            return false;
        }
    }

    private static async Task<bool> ProcessModifiedFile(UpdateFileEntry file, string targetPath, string backupPath,
        ZipFile zipFile)
    {
        if (!File.Exists(targetPath))
        {
            Console.WriteLine($"Source file does not exist: {targetPath}");
            return false;
        }

        if (!BackupFile(targetPath, backupPath))
        {
            return false;
        }

        var normalizedPath = file.Path.Replace('\\', '/') + ".patch";
        var entry = zipFile.GetEntry(normalizedPath);
        if (entry == null || entry.IsDirectory)
        {
            Console.WriteLine($"Patch file not found in ZIP archive: {file.Path}");
            RestoreFileFromBackup(targetPath, backupPath);
            return false;
        }

        using var patchStream = new MemoryStream();
        await using (var zipStream = zipFile.GetInputStream(entry))
        {
            await zipStream.CopyToAsync(patchStream);
        }

        patchStream.Position = 0;

        await using var originalFileStream = File.OpenRead(backupPath);
        await using var resultFileStream = File.Create(targetPath);

        var progress = new Progress<ProgressReport>(report =>
        {
            if (report.Total == 0) return;
            var percentage = (double)report.CurrentPosition / report.Total * 100;
            Console.Write($"\rApplying patch: {percentage:F2}% complete   ");
        });

        try
        {
            var delta = new BinaryDeltaReader(patchStream, progress);
            await new DeltaApplier { SkipHashCheck = false }.ApplyAsync(originalFileStream, delta, resultFileStream);
            return true;
        }
        finally
        {
            string clearLine = new string(' ', Console.WindowWidth - 1);
            Console.Write($"\r{clearLine}\r");
        }
    }


    private static async Task<bool> ProcessNewFile(UpdateFileEntry file, string targetPath, string backupPath,
        ZipFile zipFile)
    {
        if (File.Exists(targetPath))
        {
            if (!BackupFile(targetPath, backupPath))
            {
                return false;
            }
        }

        var normalizedPath = file.Path.Replace('\\', '/');
        var entry = zipFile.GetEntry(normalizedPath);
        if (entry == null || entry.IsDirectory)
        {
            Console.WriteLine($"New file not found in ZIP archive: {file.Path}");
            RestoreFileFromBackup(targetPath, backupPath);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        await using var zipStream = zipFile.GetInputStream(entry);
        await using var targetStream = File.Create(targetPath);
        await zipStream.CopyToAsync(targetStream);

        return true;
    }

    private static bool ProcessDeletedFile(string targetPath, string backupPath)
    {
        if (!File.Exists(targetPath))
        {
            return true; // File is already gone, consider it a success.
        }

        if (!BackupFile(targetPath, backupPath))
        {
            return false;
        }

        File.Delete(targetPath);
        return true;
    }

    private static bool BackupFile(string sourcePath, string backupPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(sourcePath, backupPath, true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to back up file {sourcePath}: {ex.Message}");
            return false;
        }
    }

    private static void RestoreFileFromBackup(string targetPath, string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath)) return;
            File.Copy(backupPath, targetPath, true);
            Console.WriteLine($"Restored file from backup: {targetPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to restore file from backup {targetPath}: {ex.Message}");
        }
    }

    private static bool ExecuteGlobalRollback(string oldFileRoot, string gameRootDir, string patchedDataFile)
    {
        try
        {
            Console.WriteLine("Starting full rollback...");

            if (!Directory.Exists(oldFileRoot))
            {
                Console.WriteLine("Backup directory not found. Cannot perform rollback.");
                return false;
            }

            var backupFiles = Directory.GetFiles(oldFileRoot, "*", SearchOption.AllDirectories);
            var totalFiles = backupFiles.Length;

            for (var i = 0; i < totalFiles; i++)
            {
                var backupFile = backupFiles[i];
                var relativePath = Path.GetRelativePath(oldFileRoot, backupFile);
                var targetPath = Path.Combine(gameRootDir, relativePath);

                Console.WriteLine($"Rolling back file [{i + 1}/{totalFiles}]: {relativePath}");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(backupFile, targetPath, true);
            }

            if (File.Exists(patchedDataFile))
            {
                File.Delete(patchedDataFile);
            }

            Console.WriteLine("Full rollback completed.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Full rollback failed: {ex.Message}");
            return false;
        }
    }
}

public enum UpdateFileEntryState
{
    Unknown,
    Modified,
    New,
    Deleted,
}

public sealed class UpdateFileEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("state")] public UpdateFileEntryState State { get; set; }
    [JsonPropertyName("patchAlgorithmId")] public byte PatchAlgorithmId { get; set; }
    [JsonPropertyName("applyTime")] public long ApplyTime { get; set; } = 10;
}

public sealed class UpdateMetadata
{
    [JsonPropertyName("files")] public List<UpdateFileEntry> Files { get; set; } = [];
    [JsonPropertyName("metadataVersion")] public byte MetadataVersion { get; set; }
    [JsonPropertyName("fromVersion")] public string FromVersion { get; set; } = string.Empty;
    [JsonPropertyName("toVersion")] public string ToVersion { get; set; } = string.Empty;
}

public sealed class RunHistory
{
    [JsonPropertyName("fromVersion")] public string FromVersion { get; set; } = string.Empty;
    [JsonPropertyName("toVersion")] public string ToVersion { get; set; } = string.Empty;
    [JsonPropertyName("startedAt")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("completedAt")] public DateTime? CompletedAt { get; set; }
    [JsonPropertyName("currentIndex")] public int CurrentIndex { get; set; }
    [JsonPropertyName("isCompleted")] public bool IsCompleted { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
}

/// <summary>
/// JSON serialization context for AOT compilation support.
/// </summary>
[JsonSerializable(typeof(RunHistory))]
[JsonSerializable(typeof(UpdateMetadata))]
[JsonSerializable(typeof(UpdateFileEntry))]
[JsonSerializable(typeof(UpdateFileEntry[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
public partial class JsonContext : JsonSerializerContext;