# VDC Coordination Revit Add-in

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
| Automation | Footing Zones | Generates transparent influence volumes for foundations/slabs. | `schemas/footing_zone.schema.json` |
| Reports ▾ | Level Report | Exports level data (host + optional linked) to CSV. | `schemas/level_report.schema.json` |
| Reports ▾ | Grid Report | Exports grid geometry (host + optional linked) to CSV. | `schemas/grid_report.schema.json` |
| Reports ▾ | Shared Coordinate Report | Compares host/link base & survey points in a pivoted CSV + HTML heatmap. | `schemas/shared_coordinates_report.schema.json` |
| Exports | NWC Batch Export | Batch-export selected 3D view sets to NWC with shared coordinates. | `schemas/nwc_batch_export.schema.json` |
| Views | Copy Linked Views | Copy selected plan/3D views or view sets from links into the host document. | UI-driven |

Schema-driven commands (Footing Zones, Level Report, Grid Report, Shared Coordinate Report, NWC Batch Export) pick up changes from their JSON defaults. Copy Linked Views is fully UI-driven and surfaces any named view sets exposed by Revit for one-click copying. Shared Coordinate Report produces both a pivoted CSV (points in rows, models in columns) and an HTML comparison view with host deltas highlighted for quick QA.

## Quick Start

### Development (Recommended)
```powershell
# 1. Build
.\build\scripts\build.ps1

# 2. In Revit:
#    - Add-Ins tab → Add-In Manager
#    - Click "Assem" → Load: src\RevitSuite.Host\bin\Release\net48\RevitSuite.Host.dll
#    - Test your changes

# 3. Make changes, rebuild, reload:
.\build\scripts\build.ps1
#    - In Add-In Manager: Click "Reload"
```

**Requirements**: [Add-In Manager](https://github.com/chuongmep/RevitAddInManager) (enables hot reload)

### Production Deployment
```powershell
# Deploy to Revit (installs to %APPDATA%\Autodesk\Revit\Addins\2025)
.\deploy.ps1

# For different Revit version (auto-resolves API dir if using the default install path):
.\deploy.ps1 -RevitYear 2024

# Explicit API folder (when Revit lives in a custom location):
.\deploy.ps1 -RevitYear 2024 -ApiDir "D:\Apps\Autodesk\Revit 2024"
```

---

## Commands

| Command | What it does |
|---------|--------------|
| `.\build\scripts\build.ps1 [-RevitYear ####] [-ApiDir path]` | **Build only** - compiles to `bin/Release/net48/`. Supplying a year/path sets `REVIT_API_DIR` before building. |
| `.\deploy.ps1 [-RevitYear ####] [-ApiDir path]` | **Build + Deploy** - builds with the resolved API directory and installs to the appropriate Revit Addins folder. |

## Workflow

### During Development
1. Start Revit
2. Load via Add-In Manager (once per session)
3. Make code changes
4. Run: `.\build\scripts\build.ps1`
5. In Add-In Manager: Click "Reload"
6. Test immediately ✓

### For Production
1. Run: `.\deploy.ps1 [-RevitYear ####]`
2. Restart Revit
3. Add-in loads automatically ✓

## Logging
Host logs are written to `%LOCALAPPDATA%\RevitSuite\logs\` with correlation IDs for each command execution.
