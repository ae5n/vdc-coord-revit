# Revit Suite

Schema-driven Revit automation hosted entirely inside a single C# add-in. The repository contains the host project, shared configuration schemas, and tooling for build/install.

## Requirements
- Windows with Autodesk Revit installed.
- `.NET Framework 4.8` targeting pack (`net48`) and `dotnet` CLI.
- `REVIT_API_DIR` environment variable pointing at the Revit API assemblies (`RevitAPI.dll`, `RevitAPIUI.dll`).

## Repository Layout
```
revit-suite/
├─ build/scripts/           # build.ps1, install.ps1, deploy.ps1
├─ docs/planning/           # implementation notes / status
├─ schemas/                 # JSON schemas powering command defaults
├─ src/RevitSuite.Host/     # C# add-in (commands, UI, logging, config helpers)
└─ README.md
```

## Commands & Defaults
| Ribbon Panel | Command | Description | Schema |
| --- | --- | --- | --- |
| Automation | Create Views | Creates a floor/ceiling plan based on schema defaults. | `schemas/create_views.schema.json` |
| Automation | Footing Zones | Generates transparent influence volumes for foundations/slabs. | `schemas/footing_zone.schema.json` |
| Reports | Level Report | Exports level data (host + optional linked) to CSV. | `schemas/level_report.schema.json` |
| Reports | Grid Report | Exports grid geometry (host + optional linked) to CSV. | `schemas/grid_report.schema.json` |

Update command behaviour by editing the corresponding schema defaults—no code changes required.

## Build & Install
```
PS C:\Dev\revit-suite> .\deploy.ps1    # build + install into %APPDATA%\Autodesk\Revit\Addins\<Year>
```
The script compiles `src/RevitSuite.Host`, copies `schemas/`, and refreshes the `.addin` manifest.

## Logging
Host logs are written to `%LOCALAPPDATA%\RevitSuite\logs\` with correlation IDs for each command execution.
