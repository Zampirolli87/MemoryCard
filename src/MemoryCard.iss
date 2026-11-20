#ifndef InstallerAppId
  #define InstallerAppId "{{E1C88AD0-C8A2-4C89-8C1B-3C73807E2161}"
#endif
#ifndef InstallerFile
  #define InstallerFile "MemoryCard-Setup"
#endif
#ifndef InstallerName
  #define InstallerName "MemoryCard"
#endif
#ifndef InstallerMutex
  #define InstallerMutex "Local\BackupHydraManual"
#endif

[Setup]
AppId={#InstallerAppId}
AppName={#InstallerName}
AppVersion=1.3.0
AppPublisher=iZampirolli
AppMutex={#InstallerMutex}
DefaultDirName={localappdata}\Programs\MemoryCard
DefaultGroupName=MemoryCard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0
WizardStyle=modern dark includetitlebar
WizardBackColor=$00180D13
SetupIconFile=MemoryCard.ico
UninstallDisplayIcon={app}\MemoryCard.exe
OutputDir=installer-build
OutputBaseFilename={#InstallerFile}
VersionInfoVersion=1.3.0.0
VersionInfoCompany=iZampirolli
VersionInfoDescription=Instalador do MemoryCard
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
BeveledLabel=Desenvolvido por iZampirolli

[CustomMessages]
DesktopShortcut=Criar um atalho na Área de Trabalho
LaunchMemoryCard=Abrir o MemoryCard

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: "MemoryCard-candidate.exe"; DestDir: "{app}"; DestName: "MemoryCard.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#InstallerName}"; Filename: "{app}\MemoryCard.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#InstallerName}"; Filename: "{app}\MemoryCard.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MemoryCard.exe"; Description: "{cm:LaunchMemoryCard}"; Flags: nowait postinstall skipifsilent unchecked

