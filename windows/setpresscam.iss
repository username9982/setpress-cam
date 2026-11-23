; セットプレス CAM（Windows 版）のインストーラー（Inno Setup 6）。windows/build_win.py が
; AppVersion、SourceDir（ポータブル版のフォルダ）、OutputDir、IconFile、DotnetRuntime を渡して作る。
; 管理者権限なしで、ユーザーごとに入れる（.NET 10 が無い PC でだけ、ランタイムのインストーラーが管理者の確認を出す）。
;
; アプリの自動更新は、このインストーラーを /SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP=1 で実行する。
; そのときは、入れ終わったらアプリを起動し直す。
; AppId は変えない（インストール済みのものを AppId で見つける）。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{89426F8E-67FB-4DA0-909E-FF0B3D05883E}
AppName=セットプレス CAM
AppVersion={#AppVersion}
AppVerName=セットプレス CAM {#AppVersion}
AppPublisher=SetPressCam
AppPublisherURL=https://github.com/username9982/setpress-cam
AppUpdatesURL=https://github.com/username9982/setpress-cam/releases
DefaultDirName={autopf}\SetPressCam
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=SetPressCam-{#AppVersion}-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\SetPressCam.exe
UninstallDisplayName=セットプレス CAM
VersionInfoVersion={#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
japanese.UninstallName=セットプレス CAM のアンインストール
japanese.AlreadyInstalled=セットプレス CAM はすでにインストールされています。
japanese.Versions=インストール済みの版: %1%nこのインストーラーの版: %2
japanese.DoUninstall=アンインストールする%nインストール済みのものを削除して終了します。
japanese.DoUpdate=更新する%nインストール済みのものに上書きします。図面と設定はそのまま残ります。
japanese.RemoveSettings=設定と作業中の図面（AppData\Roaming\SetPressCam）も削除しますか？%n保存した図面ファイルは削除しません。
japanese.InstallingDotnet=.NET 10 デスクトップ ランタイムをインストールしています...
japanese.DotnetFailed=.NET 10 デスクトップ ランタイムをインストールできませんでした（%1）。%nこのソフトには .NET 10 が必要です。https://dotnet.microsoft.com/download/dotnet/10.0 から「.NET デスクトップ ランタイム」（x64）を入れてから、もう一度インストールしてください。
japanese.FilesGroup=ファイル:
japanese.OpenWith=図面ファイル（.json）の「プログラムから開く」に追加する

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "openwith"; Description: "{cm:OpenWith}"; GroupDescription: "{cm:FilesGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; .NET 10 デスクトップ ランタイムのインストーラー: .NET 10 が無いときだけ PrepareToInstall で取り出して実行する
Source: "{#DotnetRuntime}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\セットプレス CAM"; Filename: "{app}\SetPressCam.exe"
Name: "{autoprograms}\{cm:UninstallName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\セットプレス CAM"; Filename: "{app}\SetPressCam.exe"; Tasks: desktopicon

[Registry]
; 「プログラムから開く」に出すだけ（.json の既定のアプリは変えない）
Root: HKA; Subkey: "Software\Classes\Applications\SetPressCam.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "セットプレス CAM"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\SetPressCam.exe\shell\open\command"; ValueType: string; ValueData: """{app}\SetPressCam.exe"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\SetPressCam.exe\SupportedTypes"; ValueType: string; ValueName: ".json"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.json\OpenWithList\SetPressCam.exe"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
; Windows 7 互換モード（Domino の XP 互換モードと同じく、枠の余白をなくす）。ユーザーが選んだモードがあれば残す。
; アプリも起動のとき、無ければ自分で書く。
Root: HKCU; Subkey: "Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: string; ValueName: "{app}\SetPressCam.exe"; ValueData: "~ WIN7RTM"; Flags: createvalueifdoesntexist uninsdeletevalue

[Run]
Filename: "{app}\SetPressCam.exe"; Description: "{cm:LaunchProgram,セットプレス CAM}"; Flags: nowait postinstall skipifsilent
; アプリの自動更新（/SILENT /RESTARTAPP=1）のときは、入れ終わったらアプリを起動し直す
Filename: "{app}\SetPressCam.exe"; Flags: nowait; Check: RestartApp

[Code]
const
  { このインストーラーの AppId で Inno Setup が書くキー（ユーザーごと、またはマシン全体） }
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{89426F8E-67FB-4DA0-909E-FF0B3D05883E}_is1';

function FindInstalled(var UninstallExe, Version: String): Boolean;
var
  Root: Integer;
begin
  Result := False;
  if RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', UninstallExe) then
    Root := HKCU
  else if RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', UninstallExe) then
    Root := HKLM
  else
    Exit;
  UninstallExe := RemoveQuotes(UninstallExe);
  if not RegQueryStringValue(Root, UninstallKey, 'DisplayVersion', Version) then
    Version := '?';
  Result := FileExists(UninstallExe);
end;

{ すでに入っていたら、アンインストールするか、上書きで更新するかを選ばせる。
  画面を出さないインストール（自動更新）は、いつも上書きで更新する。 }
function InitializeSetup(): Boolean;
var
  UninstallExe, Version: String;
  ResultCode: Integer;
begin
  Result := True;
  if WizardSilent or not FindInstalled(UninstallExe, Version) then
    Exit;
  case TaskDialogMsgBox(CustomMessage('AlreadyInstalled'),
                        FmtMessage(CustomMessage('Versions'), [Version, '{#AppVersion}']),
                        mbConfirmation, MB_YESNOCANCEL, [CustomMessage('DoUninstall'),
                        CustomMessage('DoUpdate')], 0) of
    IDYES:
      begin
        Exec(UninstallExe, '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
        Result := False;
      end;
    IDNO:
      Result := True;
  else
    Result := False;
  end;
end;

{ .NET 10 デスクトップ ランタイム（64 ビット）が入っているか。10.x ならどれでもよい（アプリは新しいパッチで動く）。 }
function DotnetInstalled(): Boolean;
var
  Find: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Find);
  if Result then
    FindClose(Find);
end;

{ アプリのファイルを入れる前に、ランタイムが無ければ入れる。ランタイムのインストーラーは自分で管理者の確認（UAC）を
  出すので、このインストーラーはユーザーごとのまま。3010 = 入ったが再起動が要る、1638 = もっと新しい版が入っている。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe, Params: String;
  ResultCode: Integer;
begin
  Result := '';
  if DotnetInstalled() then
    Exit;
  WizardForm.PreparingLabel.Caption := CustomMessage('InstallingDotnet');
  WizardForm.PreparingLabel.Visible := True;
  ExtractTemporaryFile(ExtractFileName('{#DotnetRuntime}'));
  Exe := ExpandConstant('{tmp}\') + ExtractFileName('{#DotnetRuntime}');
  if WizardSilent then
    Params := '/install /quiet /norestart'
  else
    Params := '/install /passive /norestart';
  if not Exec(Exe, Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
    Result := FmtMessage(CustomMessage('DotnetFailed'), [SysErrorMessage(ResultCode)])
  else if ResultCode = 3010 then
    NeedsRestart := True
  else if (ResultCode <> 0) and (ResultCode <> 1638) then
    Result := FmtMessage(CustomMessage('DotnetFailed'), [IntToStr(ResultCode)])
  else if not DotnetInstalled() then
    Result := FmtMessage(CustomMessage('DotnetFailed'), [IntToStr(ResultCode)]);
end;

{ アプリの自動更新から実行されたとき（/SILENT /RESTARTAPP=1）だけ、入れ終わったらアプリを起動し直す }
function RestartApp(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RESTARTAPP|0}') = '1');
end;

{ 設定と作業中の図面（%APPDATA%\SetPressCam）も消すか尋ねる }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  Dir := ExpandConstant('{userappdata}\SetPressCam');
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent and DirExists(Dir) then
    if MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO) = IDYES then
      DelTree(Dir, True, True, True);
end;
