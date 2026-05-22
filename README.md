# RevitSuite

Schema-driven Revit add-in for QA/QC and coordination workflows.

## Requirements
- Windows + Autodesk Revit (2024/2025/2026)
- .NET SDK 8+ with Windows desktop workloads
- .NET Framework 4.8 targeting pack for Revit 2024 builds
- Revit API path for Revit 2024 if Revit is not installed in the default location

## Essential Commands

### Dev build for Add-In Manager
Use this during development when Revit is already open and you want to reload the DLL through Add-In Manager. Works for Revit `2024`, `2025`, and `2026`.

```powershell
.\build\scripts\dev-build.ps1 -RevitYear 2025
```

Examples:
- `.\build\scripts\dev-build.ps1 -RevitYear 2024`
- `.\build\scripts\dev-build.ps1 -RevitYear 2025`
- `.\build\scripts\dev-build.ps1 -RevitYear 2026`

Load this DLL in Add-In Manager:
- Revit `2024`: `C:\Dev\revit-suite\src\RevitSuite.Host\bin\Release\net48\RevitSuite.Host.dll`
- Revit `2025` or `2026`: `C:\Dev\revit-suite\src\RevitSuite.Host\bin\Release\net8.0-windows10.0.19041\RevitSuite.Host.dll`

### Local deploy to Revit
Use this when you want the add-in copied into Revit's add-ins folder. Works for Revit `2024`, `2025`, and `2026`.

```powershell
.\deploy.ps1 -RevitYear 2025
```

Examples:
- `.\deploy.ps1 -RevitYear 2024`
- `.\deploy.ps1 -RevitYear 2025`
- `.\deploy.ps1 -RevitYear 2026`

### Bump release version
Use this when preparing an internal beta or stable release. This updates the stored version in `Directory.Build.props`.

```powershell
.\build\scripts\release.ps1 -Beta
.\build\scripts\release.ps1 -Patch
.\build\scripts\release.ps1 -Stable
.\build\scripts\release.ps1 -SetVersion 0.2.0-beta.1
```

What each command does:
- `-Beta`: if current version is `0.1.0-beta.1`, it becomes `0.1.0-beta.2`
- `-Patch`: if current version is `0.1.0-beta.1`, it becomes `0.1.1`
- `-Stable`: if current version is `0.1.0-beta.1`, it becomes `0.1.0`
- `-SetVersion 0.2.0-beta.1`: sets the version exactly to `0.2.0-beta.1`

### Build shareable installer
Use this when you want a shareable `.exe` for teammates.

```powershell
.\installer\build-installer.ps1
```

Output:
- `installer\out\RevitSuite-Setup-<version>.exe`

## Versioning
- Use semantic versions such as `0.1.0-beta.1`, `0.1.0-beta.2`, and `0.1.0`
- Keep preview builds below `1.0.0` while the add-in is still internal/beta
- `release.ps1` updates the repo's default version in `Directory.Build.props`
- `dev-build.ps1` is for development only and does not change the stored release version

Simple rules:
- Use `dev-build.ps1` while developing and testing through Add-In Manager
- Use `deploy.ps1` when you want the add-in installed into Revit locally
- Use `release.ps1` only when you want to change the stored release version
- Use `build-installer.ps1` when you want a shareable installer for teammates

Typical internal beta flow:
1. Check the current version in `Directory.Build.props`
2. Run `.\build\scripts\release.ps1 -Beta`
3. Run `.\installer\build-installer.ps1`
4. Share `installer\out\RevitSuite-Setup-<version>.exe`

Typical stable release flow:
1. Check the current version in `Directory.Build.props`
2. Run `.\build\scripts\release.ps1 -Stable`
3. Run `.\installer\build-installer.ps1`

## MCP Server

RevitSuite includes an MCP server that exposes Revit model tools to MCP-compatible AI clients.

### Setup

1. Deploy the add-in:

```powershell
.\deploy.ps1 -RevitYear 2026
```

2. Start the MCP server inside Revit:

Open Revit -> `Lewis VDC` tab -> `MCP` panel -> click `MCP Server`

3. Configure your MCP client:

```json
{
  "mcpServers": {
    "revit-suite": {
      "command": "node",
      "args": [
        "C:\\Users\\<username>\\AppData\\Roaming\\Autodesk\\Revit\\Addins\\2026\\RevitSuite\\mcp-server\\build\\index.js"
      ]
    }
  }
}
```

Notes:
- Replace `<username>` with your Windows username
- You only need one deployed `mcp-server\build\index.js` path, even if RevitSuite is installed for multiple Revit years
- MCP clients on Windows may not expand `%APPDATA%` in `args`, so use the full literal path

4. Restart your MCP client

### Stopping the server

Click `MCP Server` again in the ribbon to stop it. The server also shuts down automatically when Revit closes.

### Attribution and Upstream

RevitSuite's MCP layer is based in part on [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) by sparx-fire, used under the MIT License. Upstream MCP changes are reviewed selectively:

```powershell
.\build\scripts\review-upstream-mcp.ps1
```

Port only the changes that fit RevitSuite, then record the adapted upstream baseline:

```powershell
.\build\scripts\review-upstream-mcp.ps1 -Commit <commit> -Record
```

## Schemas
- Command defaults are in `schemas\*.schema.json`
- Build and deploy sync schemas into the host output folder

## Logs
- `%LOCALAPPDATA%\RevitSuite\logs\`
