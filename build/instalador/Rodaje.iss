; Instalador de Rodaje (Inno Setup 7). Lo compila build\instalador.ps1, que pasa Version, Origen (dist\Rodaje),
; Licencia (texto armado de LICENSE, TRADEMARK.md y el aviso de Microsoft) y, si hay certificado, Firmar.
; Por usuario y sin administrador: %LOCALAPPDATA%\Programs\Rodaje.

#ifndef Version
  #error Falta /DVersion
#endif
#ifndef Origen
  #error Falta /DOrigen
#endif
#ifndef Licencia
  #error Falta /DLicencia
#endif

[Setup]
AppId={{6C0B2F4E-5D1A-4C7B-9E3F-52A8D9104A11}
AppName=Rodaje
AppVersion={#Version}
AppVerName=Rodaje {#Version}
AppPublisher=Daniel (Dathinel)
AppPublisherURL=https://github.com/Dathinel/rodaje
AppSupportURL=https://github.com/Dathinel/rodaje/issues
AppUpdatesURL=https://github.com/Dathinel/rodaje/releases
AppCopyright=Copyright (C) 2026 Daniel (Dathinel)
VersionInfoVersion={#Version}.0
VersionInfoProductName=Rodaje
VersionInfoDescription=Instalador de Rodaje
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={userpf}\Rodaje
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile={#Licencia}
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=Rodaje
UninstallDisplayIcon={app}\app\Rodaje.exe
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputBaseFilename=Rodaje-{#Version}-instalador
#ifdef Firmar
SignTool=rodaje
SignedUninstaller=yes
#endif

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "escritorio"; Description: "Crear un acceso directo en el escritorio"; Flags: unchecked
Name: "proyectos"; Description: "Abrir los proyectos .rodaje con Rodaje"
Name: "videos"; Description: "Añadir Rodaje a «Abrir con» de videos y audios"

[Dirs]
Name: "{app}\app"; Attribs: hidden

[Files]
Source: "{#Origen}\*"; DestDir: "{app}"; Excludes: "*.pdb,rodaje-error.txt,arranque.txt,medidas.txt"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Rodaje"; Filename: "{app}\Rodaje.exe"; IconFilename: "{app}\app\Rodaje.exe"; Comment: "Editor de video"
Name: "{autodesktop}\Rodaje"; Filename: "{app}\Rodaje.exe"; IconFilename: "{app}\app\Rodaje.exe"; Comment: "Editor de video"; Tasks: escritorio

[Registry]
Root: HKA; Subkey: "Software\Classes\.rodaje"; ValueType: string; ValueName: ""; ValueData: "Rodaje.Proyecto"; Flags: uninsdeletevalue; Tasks: proyectos
Root: HKA; Subkey: "Software\Classes\.rodaje\OpenWithProgids"; ValueType: string; ValueName: "Rodaje.Proyecto"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: proyectos
Root: HKA; Subkey: "Software\Classes\Rodaje.Proyecto"; ValueType: string; ValueName: ""; ValueData: "Proyecto de Rodaje"; Flags: uninsdeletekey; Tasks: proyectos
Root: HKA; Subkey: "Software\Classes\Rodaje.Proyecto\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\app\Rodaje.exe,0"; Tasks: proyectos
Root: HKA; Subkey: "Software\Classes\Rodaje.Proyecto\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Rodaje.exe"" ""%1"""; Tasks: proyectos
Root: HKA; Subkey: "Software\Classes\Rodaje.Medio"; ValueType: string; ValueName: ""; ValueData: "Video o audio (Rodaje)"; Flags: uninsdeletekey; Tasks: videos
Root: HKA; Subkey: "Software\Classes\Rodaje.Medio\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Rodaje"; Tasks: videos
Root: HKA; Subkey: "Software\Classes\Rodaje.Medio\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Rodaje.exe"" ""%1"""; Tasks: videos
#define AbrirCon(Ext) 'Root: HKA; Subkey: "Software\Classes\.' + Ext + '\OpenWithProgids"; ValueType: string; ValueName: "Rodaje.Medio"; ValueData: ""; Flags: uninsdeletevalue; Tasks: videos'
#emit AbrirCon("mp4")
#emit AbrirCon("mkv")
#emit AbrirCon("mov")
#emit AbrirCon("m4v")
#emit AbrirCon("webm")
#emit AbrirCon("avi")
#emit AbrirCon("mts")
#emit AbrirCon("m2ts")
#emit AbrirCon("wmv")
#emit AbrirCon("3gp")
#emit AbrirCon("mp3")
#emit AbrirCon("wav")
#emit AbrirCon("m4a")
#emit AbrirCon("flac")

[Run]
Filename: "{app}\Rodaje.exe"; Description: "Abrir Rodaje"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Lo que la app escribe junto al exe (rodaje-error.txt, la marca de carpeta oculta). Los proyectos y
; %LOCALAPPDATA%\Rodaje no se tocan salvo que se pida abajo.
Type: filesandordirs; Name: "{app}\app"
Type: dirifempty; Name: "{app}"

[Code]
// Ajustes, caché y complementos en %LOCALAPPDATA%\Rodaje: se preguntan (por defecto, no). En silencio solo con /BORRARDATOS.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Datos: String;
  Borrar: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Datos := ExpandConstant('{localappdata}\Rodaje');
  if not DirExists(Datos) then Exit;
  if UninstallSilent then
    Borrar := Pos('/BORRARDATOS', Uppercase(GetCmdTail)) > 0
  else
    Borrar := MsgBox('¿Borrar también los ajustes, la caché y los complementos de Rodaje?' + #13#10 + Datos + #13#10#13#10 +
      'Tus proyectos y videos no se tocan.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if Borrar then DelTree(Datos, True, True, True);
end;
