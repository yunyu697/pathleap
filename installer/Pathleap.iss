; Pathleap Windows 安装包脚本（Inno Setup 6）
;
; 构建入口：tools\Package-LpInstaller.ps1（版本号从构建产物读取，避免第二处硬编码）。
; 手工构建示例：
;   ISCC.exe /DAppVersion=1.0.1 /DAppNumericVersion=1.0.1.0 ^
;     /DSourceDir=<stage> /DOutputDir=<dist> ^
;     /DOutputBaseName=Pathleap-1.0.1-win-x64-setup installer\Pathleap.iss
;
; 安装行为：
;   - 默认按当前用户安装（无需 UAC），安装目录可修改；
;   - 开始菜单快捷方式必装，桌面快捷方式为可选任务（默认不勾选）；
;   - 安装结束可选择立即启动 UI，UI 会按既有逻辑自动拉起同目录的 Service；
;   - 卸载移除程序文件、快捷方式与卸载注册项；配置/日志/索引缓存默认保留，
;     交互式卸载会询问是否一并删除。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Pathleap-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "Pathleap-" + AppVersion + "-win-x64-setup"
#endif

[Setup]
AppId={{3F2B8C71-9A4D-4E6B-8F21-7C5D9A1E4B63}
AppName=Pathleap
AppVersion={#AppVersion}
AppVerName=Pathleap {#AppVersion}
AppPublisher=Pathleap
AppPublisherURL=https://github.com/yunyu697/pathleap
AppSupportURL=https://github.com/yunyu697/pathleap
AppUpdatesURL=https://github.com/yunyu697/pathleap/releases
DefaultDirName={autopf}\Pathleap
DefaultGroupName=Pathleap
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Pathleap {#AppVersion}
UninstallDisplayIcon={app}\Pathleap.UI.exe
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductName=Pathleap
VersionInfoDescription=Pathleap Setup
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
AllowUNCPath=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Pathleap"; Filename: "{app}\Pathleap.UI.exe"; WorkingDir: "{app}"; Comment: "Pathleap 快速定位"
Name: "{group}\卸载 Pathleap"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Pathleap"; Filename: "{app}\Pathleap.UI.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Pathleap.UI.exe"; Description: "{cm:LaunchProgram,Pathleap}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function StopPathleapProcesses(): Boolean;
var
  LpResultCode: Integer;
begin
  { 升级/卸载前结束旧进程，避免文件占用；未运行时 taskkill 返回非零，忽略即可。 }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM Pathleap.UI.exe', '', SW_HIDE, ewWaitUntilTerminated, LpResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM Pathleap.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, LpResultCode);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopPathleapProcesses();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LpDeleteData: Boolean;
  LpDataRoot: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopPathleapProcesses();
    LpDataRoot := ExpandConstant('{localappdata}\Pathleap');
    LpDeleteData := False;
    if not UninstallSilent then
    begin
      LpDeleteData := MsgBox('是否同时删除个人配置、日志与索引缓存？' + #13#10 +
        '选择“否”将保留这些数据，重新安装后可以继续使用。' + #13#10#13#10 +
        '位置：' + LpDataRoot, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    end;

    { 安装目录可能与数据根同名，此时交给卸载器自己清理程序文件。 }
    if LpDeleteData and (CompareText(ExpandConstant('{app}'), LpDataRoot) <> 0) then
    begin
      DelTree(LpDataRoot, True, True, True);
    end;
  end;
end;
