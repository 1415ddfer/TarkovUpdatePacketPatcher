using System.Text.Json.Serialization;
using FastRsync.Delta;
using FastRsync.Signature;

namespace FastRsync.Core;

/// <summary>
/// Provides AOT-safe JSON serialization metadata for FastRsync types.
/// </summary>
[JsonSerializable(typeof(DeltaMetadata))]
[JsonSerializable(typeof(SignatureMetadata))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
public partial class JsonContextCore : JsonSerializerContext;