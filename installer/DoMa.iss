#ifndef ExtensionId
  #define ExtensionIdFile FileOpen(SourcePath + "extension-id.txt")
  #define ExtensionId FileRead(ExtensionIdFile)
  #expr FileClose(ExtensionIdFile)
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppName=Download Manager (DoMa)
AppVersion={#AppVersion}
AppPublisher=DoMa
VersionInfoVersion={#AppVersion}
AppId={{6F3C1B7E-5A2D-4C8B-9E41-D0A7B2C5F813}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\DoMa
DefaultGroupName=Download Manager (DoMa)
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=DoMa-Setup-{#AppVersion}
SetupIconFile=..\src\DoMa.App\Assets\doma.ico
Compression=lzma2
SolidCompression=yes
AppMutex=Local\DoMa.SingleInstance
UninstallDisplayIcon={app}\DoMa.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "autostart"; Description: "Chạy DoMa khi mở máy"; Flags: unchecked

[Files]
Source: "..\artifacts\app\DoMa.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\extension\*"; DestDir: "{app}\extension"; Excludes: "node_modules,test,package.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README-cai-tien-ich.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Download Manager (DoMa)"; Filename: "{app}\DoMa.exe"

[Registry]
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.doma.host"; ValueType: string; ValueName: ""; ValueData: "{app}\com.doma.host.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.doma.host"; ValueType: string; ValueName: ""; ValueData: "{app}\com.doma.host.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DoMa"; ValueData: """{app}\DoMa.exe"" --nen"; Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DoMa"; Flags: uninsdeletevalue dontcreatekey

[Run]
; Sau khi tự cập nhật (DoMa gọi bộ cài với /UPDATE=1), mở lại ứng dụng.
Filename: "{app}\DoMa.exe"; Flags: nowait; Check: IsUpdate
Filename: "{app}\README-cai-tien-ich.txt"; Description: "Xem hướng dẫn cài tiện ích cho trình duyệt"; Flags: postinstall shellexec skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\com.doma.host.json"

[Code]
function IsUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function InitializeSetup(): Boolean;
var
  FindRec: TFindRec;
  Found: Boolean;
  Waited: Integer;
begin
  { Khi tự cập nhật, DoMa vừa được yêu cầu thoát: đợi tối đa 30 giây cho nó đóng hẳn. }
  if IsUpdate() then
  begin
    Waited := 0;
    while CheckForMutexes('Local\DoMa.SingleInstance') and (Waited < 30000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;
  Found := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Found := True;
      until Found or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  Result := Found;
  if not Found then
    MsgBox('Máy chưa có .NET 10 Desktop Runtime. Hãy cài từ https://dotnet.microsoft.com/download/dotnet/10.0 rồi chạy lại bộ cài.', mbError, MB_OK);
end;

function JsonEscape(const S: String): String;
var
  I: Integer;
  C: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := Ord(S[I]);
    if S[I] = #92 then
      Result := Result + #92#92
    else if S[I] = '"' then
      Result := Result + #92 + '"'
    else if (C < 32) or (C > 126) then
      Result := Result + #92 + 'u' + Format('%.4x', [C])
    else
      Result := Result + S[I];
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Json: String;
begin
  if CurStep = ssPostInstall then
  begin
    Json := '{"name":"com.doma.host","description":"Download Manager (DoMa)","path":"' +
      JsonEscape(ExpandConstant('{app}\DoMa.exe')) +
      '","type":"stdio","allowed_origins":["chrome-extension://{#ExtensionId}/"]}';
    SaveStringToFile(ExpandConstant('{app}\com.doma.host.json'), Json, False);
  end;
end;
