# RevitSuite

Schema-driven Revit add-in (`net48`) for QA/QC and coordination workflows.

## Requirements
- Windows + Autodesk Revit (2024/2025).
- .NET Framework 4.8 targeting pack + `dotnet` CLI.
- Revit API path (auto-resolved by scripts when Revit is in default install location).

## Build
```powershell
.\build\scripts\build.ps1
.\build\scripts\build.ps1 -RevitYear 2024
.\build\scripts\build.ps1 -RevitYear 2024 -ApiDir "D:\Apps\Autodesk\Revit 2024"
```

## Deploy (local)
```powershell
.\deploy.ps1
.\deploy.ps1 -RevitYear 2024
.\deploy.ps1 -RevitYear 2024 -ApiDir "D:\Apps\Autodesk\Revit 2024"
```

## Installer (shareable EXE)
```powershell
# Builds version-specific payloads for 2024 + 2025 and packages one installer.
.\installer\build-installer.ps1
```

Output:
- `installer\out\RevitSuite-Setup.exe`

## Schemas
- Command defaults are in `schemas/*.schema.json`.
- Build/deploy sync schemas into host output under `src/RevitSuite.Host\bin\<Config>\net48\schemas\`.

## Logs
- `%LOCALAPPDATA%\RevitSuite\logs\`
