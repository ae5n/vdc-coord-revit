# Revit Suite — Quick Start

## Prereqs
- Windows with Revit installed.
- `REVIT_API_DIR` env var pointing at the Revit API DLL folder.
- .NET Framework 4.8 targeting pack + `dotnet`.
- Python 3.x reachable via `py`, `python`, or `REVITSUITE_PYTHON`. Pass `-PythonExe` to `engine.ps1` if you need a custom path.

## Run
```
PS C:\Dev\revit-suite> .\deploy.ps1    # build + install (rerun after code changes)
PS C:\Dev\revit-suite> .\engine.ps1    # start the Python engine (leave running, add -PythonExe if needed)
```
Open Revit and use the **Revit Suite → Automation** ribbon buttons (`Create Views`, `Level Report`, `Grid Report`).

Logs: `%LOCALAPPDATA%\RevitSuite\logs\` (host) and the engine console.
