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
  #define Source2025 "..\src\RevitSuite.Host\bin\Release\net48"
#endif

[Setup]
AppId={{E9D0978C-6E4E-4D2E-A7F8-1A56E2453A9A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={userappdata}\Autodesk\Revit\Addins
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={userappdata}\Autodesk\Revit\Addins\2025\RevitSuite\RevitSuite.Host.dll
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

[Files]
Source: "{#Source2024}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\RevitSuite"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Tasks: revit2024
Source: "{#Source2025}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\RevitSuite"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Tasks: revit2025

[UninstallDelete]
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitSuite.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitSuite.addin"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitSuite"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitSuite"

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
  end;
end;
