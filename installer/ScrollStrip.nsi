Unicode true
RequestExecutionLevel user
!include MUI2.nsh
!include x64.nsh
!include "LegacyRuntime.nsh"

; Build with NSIS 3 after dotnet publish ScrollStrip.csproj -c Release -o publish
!ifndef PAYLOAD
  !define PAYLOAD "../publish"
!endif
!ifndef OUTPUT
  !define OUTPUT "ScrollStrip-1.1.2-win-x64-Setup.exe"
!endif
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ScrollStrip.User"
Name "ScrollStrip 1.1.2"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\ScrollStrip"
SetCompressor /SOLID lzma
VIProductVersion "1.1.2.0"
VIAddVersionKey "ProductName" "ScrollStrip"
VIAddVersionKey "ProductVersion" "1.1.2"
VIAddVersionKey "FileVersion" "1.1.2.0"
VIAddVersionKey "FileDescription" "ScrollStrip 安装程序"
VIAddVersionKey "LegalCopyright" "ScrollStrip contributors"
!define MUI_ICON "../Resources/tray.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\app\ScrollStrip.exe"
!define MUI_FINISHPAGE_RUN_PARAMETERS "--settings"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

; Older and portable builds use the same per-user instance mutex.
!macro CheckInstance
  checkRunning:
    nsExec::ExecToStack `"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "$$m = $$null; if ([Threading.Mutex]::TryOpenExisting(('Local\ScrollStrip.Instance.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value), [ref]$$m)) { exit 23 }"`
    Pop $0
    Pop $1
    StrCmp $0 23 0 doneChecking
    MessageBox MB_RETRYCANCEL|MB_ICONINFORMATION "请先右键系统托盘中的 ScrollStrip 并退出，然后点“重试”。" /SD IDCANCEL IDRETRY checkRunning
    Abort
  doneChecking:
!macroend

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK "此安装包需要 64 位 Windows。"
    Abort
  ${EndIf}
  !insertmacro CheckInstance
FunctionEnd

Function un.onInit
  !insertmacro CheckInstance
FunctionEnd

Section
  SetOutPath "$INSTDIR\app"
  File "${PAYLOAD}/ScrollStrip.exe"
  File "${PAYLOAD}/ScrollStrip.dll"
  File "${PAYLOAD}/ScrollStrip.deps.json"
  File "${PAYLOAD}/ScrollStrip.runtimeconfig.json"
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "DisplayVersion"
  ReadRegStr $1 HKCU "${UNINSTALL_KEY}" "UninstallString"
  ${If} $0 == "1.1.0"
  ${AndIf} $1 == '"$INSTDIR\Uninstall.exe"'
    !insertmacro RemoveLegacyRuntime
  ${EndIf}
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\ScrollStrip.lnk" "$INSTDIR\app\ScrollStrip.exe" "--settings"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "ScrollStrip"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "1.1.2"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\app\ScrollStrip.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ScrollStrip"
  StrCmp $0 '"$INSTDIR\app\ScrollStrip.exe"' 0 +2
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ScrollStrip"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  Delete "$SMPROGRAMS\ScrollStrip.lnk"
  Delete "$INSTDIR\app\ScrollStrip.exe"
  Delete "$INSTDIR\app\ScrollStrip.dll"
  Delete "$INSTDIR\app\ScrollStrip.deps.json"
  Delete "$INSTDIR\app\ScrollStrip.runtimeconfig.json"
  RMDir "$INSTDIR\app"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
