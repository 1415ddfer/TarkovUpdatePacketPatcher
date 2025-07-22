# 游戏补丁工具 / Game Patching Tool

![GitHub](https://img.shields.io/badge/.NET-9.0-blueviolet)
![Platform](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey)

## 中文说明 / English Description

### 项目概述 / Project Overview
这是一个用于游戏文件修补的.NET工具。  
This is a .NET tool for patching game files, featuring automatic backup and version management.

---

### 使用方法 / Usage Instructions

#### 1. 克隆项目 / Clone the Project
```bash
git clone https://github.com/1415ddfer/TarkovUpdatePacketPatcher.git
```

#### 2. 发布应用程序 / Publish the Application
使用Visual Studio或.NET CLI执行以下发布操作：  
Use Visual Studio or .NET CLI with these settings:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishTrimmed=true -p:PublishSingleFile=true
```

#### 3. 定位可执行文件 / Locate the Executable
发布后文件路径：  
Path after publishing:
```
\bin\Release\net9.0-windows\win-x64\publish\ESPPatcher.exe
```

#### 4. 运行程序 / Run the Application
双击可执行文件，根据控制台提示操作  
Double-click the executable and follow console instructions

---

### 更新机制 / Update Mechanism
- 每次更新时，源文件自动备份到：  
  Source files are automatically backed up to:
  ```
  游戏根目录/LastUpdateFile
  GameRoot/LastUpdateFile
  ```
  
- 备份目录结构示例：  
  Backup directory example:
  ```
  LastUpdateFile/
  ├── LastRunHistory.json
  └── Old Game Files..
  ```
---

### 重要注意事项 / Important Notes
⚠️ **在修补前必须**  
⚠️ **Before patching you MUST:**
1. 验证游戏文件的完整性  
   Verify game file integrity
2. 确认已下载正确版本的更新包  
   Ensure correct update package version is downloaded
3. 使用新的更新包会将上次备份的文件删除  
   Using the new update package will delete the files that were backed up last time.

---

## 开发说明 / Development Notes
### 推荐环境 / Recommended Environment
- Visual Studio 2022+ or rider2025.1.4+
- .NET 9 SDK
- Windows 10/11 x64

### 构建参数参考 / Build Parameters Reference
| 参数 | 值 | 作用 |
|------|-----|------|
| `--self-contained` | true | 包含所有依赖 |
| `-p:PublishReadyToRun` | true | 预编译提升启动速度 |
| `-p:PublishTrimmed` | true | 移除未使用程序集 |
| `-p:PublishSingleFile` | true | 生成单文件 |

---

> **免责声明**: 使用本工具造成的任何文件损坏风险由用户自行承担。  
> **Disclaimer**: User assumes all risks for potential file corruption.
