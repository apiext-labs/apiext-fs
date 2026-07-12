; Apiext.Fs Windows installer (NSIS) — installs as a Windows Service
; Build:
;   makensis /DPRODUCT_VERSION=260412-1530 /DSOURCE_DIR=..\..\publish\app /DOUTFILE=..\..\publish\Apiext.Fs-Setup.exe apiext-fs.nsi

!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "0.0.0"
!endif

!ifndef SOURCE_DIR
  !error "SOURCE_DIR is required (folder containing Apiext.Fs.exe)"
!endif

!ifndef OUTFILE
  !define OUTFILE "Apiext.Fs-Setup.exe"
!endif

!define PRODUCT_NAME "Apiext.Fs"
!define PRODUCT_PUBLISHER "Apiext.Fs"
!define PRODUCT_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\Apiext.Fs.exe"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define PRODUCT_UNINST_ROOT_KEY "HKLM"
!define INSTALL_DIR "$PROGRAMFILES64\Apiext.Fs"
!define SERVICE_NAME "ApiextFs"
!define SERVICE_DISPLAY "Apiext.Fs"
!define SERVICE_DESC "Apiext.Fs S3-compatible file storage service"
!define SERVICE_PORT "8080"

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel admin

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTFILE}"
InstallDir "${INSTALL_DIR}"
InstallDirRegKey HKLM "${PRODUCT_DIR_REGKEY}" ""
ShowInstDetails show
ShowUnInstDetails show

!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

Var InstallAsService

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom ServicePageCreate ServicePageLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_NOAUTOCLOSE
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION FinishRun
!define MUI_FINISHPAGE_RUN_TEXT "打开 Web 控制台"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Apiext.Fs 需要 64 位 Windows。"
    Abort
  ${EndIf}
  SetRegView 64
  StrCpy $InstallAsService "1"
FunctionEnd

; ── Custom page: install as Windows Service? ─────────────────────────
Var HwndServiceCheck

Function ServicePageCreate
  !insertmacro MUI_HEADER_TEXT "运行方式" "选择安装后的运行方式"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 36u "建议安装为 Windows 服务，以便开机自启并在后台运行（无需保持登录）。也可仅安装程序文件，稍后手动启动。"
  Pop $1

  ${NSD_CreateCheckbox} 0 48u 100% 12u "安装并启动 Windows 服务（推荐）"
  Pop $HwndServiceCheck
  ${NSD_Check} $HwndServiceCheck

  ${NSD_CreateLabel} 0 72u 100% 48u "服务名：${SERVICE_NAME}$\r$\n监听地址：http://0.0.0.0:${SERVICE_PORT}$\r$\nWeb 后台：http://localhost:${SERVICE_PORT}/_apiextfs$\r$\n数据目录：安装目录\App_Data"
  Pop $2

  nsDialogs::Show
FunctionEnd

Function ServicePageLeave
  ${NSD_GetState} $HwndServiceCheck $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $InstallAsService "1"
  ${Else}
    StrCpy $InstallAsService "0"
  ${EndIf}
FunctionEnd

Function FinishRun
  ExecShell "open" "http://localhost:${SERVICE_PORT}/_apiextfs"
FunctionEnd

; ── Helpers ──────────────────────────────────────────────────────────
!macro StopAndDeleteService
  nsExec::ExecToLog 'sc.exe stop "${SERVICE_NAME}"'
  Sleep 1500
  nsExec::ExecToLog 'sc.exe delete "${SERVICE_NAME}"'
  Sleep 1000
!macroend

Section "Install" SEC01
  SetOutPath "$INSTDIR"
  SetOverwrite on

  ; Stop existing service before overwriting binaries
  !insertmacro StopAndDeleteService

  ; Application payload
  File /r "${SOURCE_DIR}\*.*"

  ; Windows Service defaults (Urls / logging). Optional; app has code defaults too.
  IfFileExists "$INSTDIR\appsettings.WindowsService.json" 0 +2
    DetailPrint "Using bundled appsettings.WindowsService.json"

  CreateDirectory "$INSTDIR\App_Data"

  ; Shortcuts
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME} 控制台.lnk" "http://localhost:${SERVICE_PORT}/_apiextfs"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\启动服务.lnk" "$SYSDIR\sc.exe" "start ${SERVICE_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\停止服务.lnk" "$SYSDIR\sc.exe" "stop ${SERVICE_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "http://localhost:${SERVICE_PORT}/_apiextfs"

  ; Uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Registry
  WriteRegStr HKLM "${PRODUCT_DIR_REGKEY}" "" "$INSTDIR\Apiext.Fs.exe"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayName" "$(^Name)"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\Apiext.Fs.exe"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "InstallAsService" "$InstallAsService"

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "EstimatedSize" "$0"

  ; Register Windows Service
  ${If} $InstallAsService == "1"
    DetailPrint "Creating Windows service ${SERVICE_NAME}..."
    ; binPath must be quoted when path has spaces; trailing space after = is required by sc.exe
    nsExec::ExecToLog 'sc.exe create "${SERVICE_NAME}" binPath= "\"$INSTDIR\Apiext.Fs.exe\"" start= auto DisplayName= "${SERVICE_DISPLAY}" obj= LocalSystem'
    Pop $0
    DetailPrint "sc create exit: $0"

    nsExec::ExecToLog 'sc.exe description "${SERVICE_NAME}" "${SERVICE_DESC}"'
    Pop $0

    ; Failure restart policy: restart after 5s / 30s / 60s
    nsExec::ExecToLog 'sc.exe failure "${SERVICE_NAME}" reset= 86400 actions= restart/5000/restart/30000/restart/60000'
    Pop $0

    DetailPrint "Starting service..."
    nsExec::ExecToLog 'sc.exe start "${SERVICE_NAME}"'
    Pop $0
    DetailPrint "sc start exit: $0"

    ${If} $0 != 0
      MessageBox MB_ICONEXCLAMATION "服务已创建，但启动失败（错误码 $0）。$\r$\n可稍后在「服务」管理器中手动启动 ${SERVICE_NAME}，或查看 Windows 事件日志。"
    ${EndIf}
  ${EndIf}
SectionEnd

Section "Uninstall"
  SetRegView 64

  ; Stop & remove service first
  !insertmacro StopAndDeleteService

  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME} 控制台.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\启动服务.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\停止服务.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"

  ; Remove program files, keep App_Data to avoid data loss
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\Apiext.Fs.exe"
  Delete "$INSTDIR\appsettings.json"
  Delete "$INSTDIR\appsettings.WindowsService.json"
  Delete "$INSTDIR\README.txt"
  Delete "$INSTDIR\*.pdb"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.txt"

  RMDir /r "$INSTDIR\wwwroot"
  RMDir /r "$INSTDIR\runtimes"
  RMDir "$INSTDIR"

  DeleteRegKey ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKLM "${PRODUCT_DIR_REGKEY}"
SectionEnd
