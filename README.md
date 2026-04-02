# RevitSuite

Schema-driven Revit add-in for QA/QC and coordination workflows.

## Requirements
- Windows + Autodesk Revit (2024/2025/2026).
- .NET SDK 8+ with Windows desktop workloads.
- .NET Framework 4.8 targeting pack for Revit 2024 builds.
- Revit API path (auto-resolved by scripts when Revit is in default install location).

## Build
```powershell
.\build\scripts\build.ps1
.\build\scripts\build.ps1 -Version 0.1.0-beta.1
.\build\scripts\build.ps1 -RevitYear 2024
.\build\scripts\build.ps1 -RevitYear 2026
.\build\scripts\build.ps1 -RevitYear 2024 -ApiDir "D:\Apps\Autodesk\Revit 2024"
```

## Deploy (local)
```powershell
.\deploy.ps1
.\deploy.ps1 -Version 0.1.0-beta.1
.\deploy.ps1 -RevitYear 2024
.\deploy.ps1 -RevitYear 2026
.\deploy.ps1 -RevitYear 2024 -ApiDir "D:\Apps\Autodesk\Revit 2024"
```

## Installer (shareable EXE)
```powershell
# Builds version-specific payloads for 2024 + 2025 + 2026 and packages one installer.
.\installer\build-installer.ps1
.\installer\build-installer.ps1 -Version 0.1.0-beta.1
```

Output:
- `installer\out\RevitSuite-Setup-<version>.exe`

## Versioning
- Use semantic versions: `0.1.0-beta.1`, `0.1.0-beta.2`, `0.1.0`, `0.2.0`.
- Keep preview builds below `1.0.0` while the add-in is still internal/beta.
- Pass the version through the scripts so the assemblies and installer stay aligned.
- Use the release helper to bump the repo version without editing XML manually:
```powershell
.\build\scripts\release.ps1 -Beta
.\build\scripts\release.ps1 -Patch
.\build\scripts\release.ps1 -Stable
.\build\scripts\release.ps1 -SetVersion 0.2.0-beta.1
```
- Recommended internal release flow:
  - `main` stays releasable.
  - Build preview installers for testers with `-Version 0.x.y-beta.n`.
  - Collect feedback/fixes.
  - Promote the same change set to a stable `0.x.y` or `1.0.0` when ready.

## MCP Server

RevitSuite includes an MCP server that exposes Revit model tools to any MCP-compatible AI client.

### How it works

```
MCP client  →  MCP server (Node.js, stdio)  →  TCP:8080  →  RevitSuite add-in  →  Revit
```

### Setup

**1. Deploy the add-in** (if not already done):
```powershell
.\deploy.ps1 -RevitYear 2026
```

**2. Start the MCP server inside Revit**

Open Revit → **Lewis VDC** tab → **MCP** panel → click **MCP Server**.
A dialog confirms the server is running on port 8080.

**3. Configure your MCP client**

Point your MCP client at the deployed server. Example config:

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

Replace `<username>` with your Windows username. The path can point to any deployed Revit year — the MCP server is version-agnostic and connects to whichever Revit is running on port 8080.

> **Note:** MCP clients on Windows may not expand `%APPDATA%` in `args` — use the full literal path.

**4. Restart your MCP client**

### Available tools

| Tool | Description |
|---|---|
| `run_qaqc` | Export control points or import field survey CSV and place deviation tags |
| `run_level_report` | Export a CSV of levels across host and linked models |
| `run_grid_report` | Export a CSV of grid lines with coordinates and angles |
| `run_shared_coordinates_report` | Export shared coordinate data for host and links |
| `run_footing_zones` | Create 3D influence zones around structural foundations |
| `run_nwc_batch_export` | Export 3D views to Navisworks NWC files |
| `say_hello` | Verify the connection is working |
| *(+ 22 more Revit model tools)* | Element creation, filtering, tagging, data extraction, etc. |

### Stopping the server

Click **MCP Server** again in the ribbon to stop it. The server also shuts down automatically when Revit closes.

## Schemas
- Command defaults are in `schemas/*.schema.json`.
- Build/deploy sync schemas into host output under `src/RevitSuite.Host\bin\<Config>\<TargetFramework>\schemas\`.

## Logs
- `%LOCALAPPDATA%\RevitSuite\logs\`

## Credits
MCP infrastructure and 23 base Revit tools adapted from [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) by sparx-fire, used under the MIT License.
