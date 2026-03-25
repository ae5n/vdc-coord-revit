; Quick shareable installer for RevitSuite (per-user install).
; Requires Inno Setup 6 (ISCC.exe).

#define AppName "RevitSuite"
#define AppVersion "1.0.0"
#define Publisher "Revit Suite"
#define ExeName "RevitSuite-Setup"
#ifndef Source2024
  #define Source2024 "..\src\RevitSuite.Host\bin\Release\net48"
#endif
#ifndef Source2025
  #define Source2025 "..\src\RevitSuite.Host\bin\Release\net8.0-windows10.0.19041"
#endif
#ifndef Source2026
  #define Source2026 "..\src\RevitSuite.Host\bin\Release\net8.0-windows10.0.19041"
#endif

[Setup]
AppId={{E9D0978C-6E4E-4D2E-A7F8-1A56E2453A9A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={userappdata}\Autodesk\Revit\Addins
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={userappdata}\Autodesk\Revit\Addins\2026\RevitSuite\RevitSuite.Host.dll
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
OutputDir=.\out
OutputBaseFilename={#ExeName}
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "revit2024"; Description: "Install for Revit 2024"; GroupDescription: "Target Revit Versions:"; Flags: checkedonce
Name: "revit2025"; Description: "Install for Revit 2025"; GroupDescription: "Target Revit Versions:"; Flags: checkedonce
Name: "revit2026"; Description: "Install for Revit 2026"; GroupDescription: "Target Revit Versions:"; Flags: checkedonce

[Files]
; Keep the shared Node dependency tree stable across upgrades. The MCP server build
; is version-agnostic and replacing native modules like better_sqlite3.node is what
; tends to fail when node.exe is still running.
Source: "{#Source2024}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\RevitSuite"; Excludes: "mcp-server\node_modules\*"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Tasks: revit2024
Source: "{#Source2025}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\RevitSuite"; Excludes: "mcp-server\node_modules\*"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Tasks: revit2025
Source: "{#Source2026}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\RevitSuite"; Excludes: "mcp-server\node_modules\*"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Tasks: revit2026

Source: "{#Source2024}\mcp-server\node_modules\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\RevitSuite\mcp-server\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist onlyifdoesntexist; Tasks: revit2024
Source: "{#Source2025}\mcp-server\node_modules\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\RevitSuite\mcp-server\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist onlyifdoesntexist; Tasks: revit2025
Source: "{#Source2026}\mcp-server\node_modules\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\RevitSuite\mcp-server\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist onlyifdoesntexist; Tasks: revit2026

[UninstallDelete]
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitSuite.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitSuite.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitSuite.addin"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitSuite"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitSuite"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitSuite"

[Code]
function BuildAddinManifest(const Year: string): string;
var
  DllPath: string;
  NewLine: string;
begin
  DllPath := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Year + '\RevitSuite\RevitSuite.Host.dll');
  NewLine := #13#10;

  Result :=
    '<?xml version="1.0" encoding="utf-8" standalone="no"?>' + NewLine +
    '<RevitAddIns>' + NewLine +
    '  <AddIn Type="Application">' + NewLine +
    '    <Name>RevitSuite</Name>' + NewLine +
    '    <Assembly>' + DllPath + '</Assembly>' + NewLine +
    '    <AddInId>00000000-0000-0000-0000-000000000100</AddInId>' + NewLine +
    '    <FullClassName>RevitSuite.Host.App</FullClassName>' + NewLine +
    '    <VendorId>RSUT</VendorId>' + NewLine +
    '    <VendorDescription>Revit Suite</VendorDescription>' + NewLine +
    '  </AddIn>' + NewLine +
    '</RevitAddIns>' + NewLine;
end;

procedure WriteAddinManifest(const Year: string);
var
  AddinPath: string;
begin
  AddinPath := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Year + '\RevitSuite.addin');
  SaveStringToFile(AddinPath, BuildAddinManifest(Year), False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('revit2024') then
      WriteAddinManifest('2024');

    if WizardIsTaskSelected('revit2025') then
      WriteAddinManifest('2025');

    if WizardIsTaskSelected('revit2026') then
      WriteAddinManifest('2026');
  end;
end;
