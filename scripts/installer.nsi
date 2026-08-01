; NSIS installer script for ChillHub (per-user install)
; Encoding: UTF-8

Unicode true
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define APP_NAME "ChillHub"
!define COMPANY_NAME "ChillHub"
!define APP_EXE "ChillHub.exe"
; ЗНАЧЕНИЕ ПО УМОЛЧАНИЮ для страницы выбора каталога — и только оно.
;
; И5: раньше эта константа подставлялась ПОВСЮДУ — в секции установки, в
; ярлыки, в реестр, в деинсталляцию. Вместе с подключённой страницей
; MUI_PAGE_DIRECTORY это давало прямой обман пользователя: он выбирал,
; например, D:\ChillHub, установщик показывал этот путь, а файлы всё равно
; уезжали в $LOCALAPPDATA\ChillHub. Выбор не игнорировался тихо — он
; игнорировался ПОСЛЕ того, как его подтвердили.
;
; Теперь везде используется $INSTDIR (его и заполняет страница выбора), а эта
; константа осталась ровно там, где ей место: в InstallDir как значение по
; умолчанию.
!define INSTALL_DIR "$LOCALAPPDATA\ChillHub"
!define UNINST_KEY "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ChillHub"
!define APP_REG "Software\\ChillHub\\Install"

; APP_VERSION — версия, которая будет записана в launcher.version (см. секцию
; Install ниже). Значение приходит СНАРУЖИ: scripts/build-installer.ps1 передаёт
; /DAPP_VERSION=<версия>.
;
; Здесь намеренно НЕТ дефолта. Раньше стояло `!define APP_VERSION "1.1.7"`, и это
; была не косметика, а блокер релиза: build-installer.ps1 передавал в makensis
; только /DPAYLOAD_DIR, поэтому любая сборка — какой бы код в неё ни попал —
; объявляла себя версией 1.1.7. Свежеустановленный лаунчер читал launcher.version,
; видел в manifests/launcher/latest.json например 1.1.8, скачивал обновление,
; после которого маркер снова оказывался равен 1.1.7 (апдейтер его не трогает,
; это preserve-файл), и предлагал обновиться опять — вечный цикл обновления.
;
; Молчаливый дефолт здесь опаснее ошибки сборки: он выпускает НЕПРАВИЛЬНО
; помеченный инсталлятор, и узнают об этом пользователи. Поэтому — !error.
!ifndef APP_VERSION
  !error "APP_VERSION is not defined. Build through scripts/build-installer.ps1 (it passes /DAPP_VERSION=...), or pass it by hand: makensis /DAPP_VERSION=1.2.3 /DPAYLOAD_DIR=... installer.nsi"
!endif

; Branding icons (paths relative to this .nsi file in scripts/)
!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
Icon "app.ico"
UninstallIcon "app.ico"

; Directory whose contents are packaged into the installer.
;
; This used to be hardcoded to ..\launcher\ChillHub\bin\Release\net8.0-windows
; while scripts/build-installer.ps1 accepts -Configuration and -Publish. Asking
; for a Debug build therefore compiled Debug and then silently packaged
; whatever happened to be lying in bin\Release — a stale Release build, or
; nothing at all. build-installer.ps1 now passes the directory it actually
; built with /DPAYLOAD_DIR=...; the !ifndef keeps a bare `makensis installer.nsi`
; working exactly as before.
!ifndef PAYLOAD_DIR
  !define PAYLOAD_DIR "..\launcher\ChillHub\bin\Release\net8.0-windows"
!endif

; Prerequisite installer filenames (centralized)
!define PREREQ_WEBVIEW2 "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
!define PREREQ_DOTNET   "windowsdesktop-runtime-8.0.20-win-x64.exe"

Var GAMES_DIR
Var GamesDir_Edit
Var GamesDir_Browse
Var GamesDir_Label
Var DeleteGames_Check
Var Un_GamesDir
Var DeleteGames_State
Var LaunchAfterFlag
Var PrereqsRan

; Output installer
Name "${APP_NAME}"
OutFile "generated_downloads\ChillHub-Setup.exe"

; Per-user installation (no admin)
RequestExecutionLevel user

; Compression
SetCompress auto
SetCompressor lzma
SetCompressorDictSize 16
SetDatablockOptimize on

; MUI options (simple modern touches)
!define MUI_ABORTWARNING
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT

; Finish page settings: show prerequisites first, then run app
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Установить зависимости (.NET 8 Desktop, WebView2)"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION InstallPrereqs
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Запустить ${APP_NAME}"
!define MUI_FINISHPAGE_RUN_FUNCTION RunAppAfterInstall

; Page sequence
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page Custom SelectGamesDir_Create SelectGamesDir_Leave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; Uninstall pages
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage Custom un.SelectDeleteGames_Create un.SelectDeleteGames_Leave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

; Default installation directory
InstallDir "${INSTALL_DIR}"

; ------------------------
; Sections
; ------------------------
Section "Install"
  ; Ensure install dir
  CreateDirectory "$INSTDIR"
  SetOutPath "$INSTDIR"

  ; Files from build output (default: Release)
  ; NOTE: build-installer.ps1 builds Release by default.
  ; Adjust path if you need Debug or Publish output.
  ; If you publish self-contained, update the path accordingly.
  ; Package framework-dependent build output (requires .NET 8 Desktop Runtime)
  ;
  ; A3/A9: из пакета исключаются
  ;   config.json      — пользовательская настройка (живёт в %APPDATA%, апдейтер её не трогает);
  ;   launcher.version — маркер версии, он пишется ниже явно (иначе разъедется с манифестом);
  ;   launcher.update-status — состояние последнего обновления, его пишет апдейтер
  ;                      в каталоге установки; тоже preserve-файл;
  ;   *.pdb            — отладочные символы, в релизе не нужны;
  ;   linux-* / osx-*  — нативные библиотеки из runtimes\ не под Windows (мёртвый вес);
  ;   Uninstall.exe    — Б6: артефакт времени установки, его пишет WriteUninstaller
  ;                      ниже. Если бы он приехал из PAYLOAD_DIR, он попал бы и в
  ;                      манифест публикуемой сборки — а апдейтер его не
  ;                      перезаписывает, что даёт вечный цикл обновления.
  ; Список исключений обязан совпадать с ChillHub.Update.PreserveMatcher.DefaultRules
  ; и со staging-фильтром в scripts/build-installer.ps1 (New-LauncherPayload).
  File /r /x "config.json" /x "launcher.version" /x "launcher.update-status" /x "Uninstall.exe" /x "*.pdb" /x "linux-*" /x "osx-*" "${PAYLOAD_DIR}\*.*"

  ; Write uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Shortcuts
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  ; Uninstall registry (per-user)
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  ; Один слэш, а не два: NSIS не обрабатывает \\ как escape, и в реестр
  ; уезжала строка с задвоенным разделителем.
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${COMPANY_NAME}"

  ; И28: поля, которые «Установка и удаление программ» ожидает увидеть.
  ;
  ; DisplayVersion отсутствовал (был закомментирован с заглушкой 1.0.0), из-за
  ; чего в списке программ у ChillHub была пустая колонка версии — и понять,
  ; что именно установлено, штатными средствами Windows было нельзя. Значение
  ; берём из той же ${APP_VERSION}, что пишется в launcher.version, чтобы
  ; реестр и маркер версии не разъезжались (см. Б8).
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"

  ; NoModify/NoRepair: у установщика нет ни режима изменения, ни режима
  ; восстановления. Без этих флагов Windows показывает кнопки «Изменить» и
  ; «Восстановить», которые запускают обычную установку заново — поведение,
  ; которого пользователь не просил.
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ; EstimatedSize (в КиБ) считается ПОСЛЕ распаковки файлов — иначе считать
  ; было бы нечего. Без него Windows показывает пустой размер.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"

  ; Persist selected GamesDir in our app registry for uninstall
  WriteRegStr HKCU "${APP_REG}" "GamesDir" "$GAMES_DIR"

  ; Ensure GamesDir exists
  IfFileExists "$GAMES_DIR" +2 0
    CreateDirectory "$GAMES_DIR"

  ; Write config.json with GamesPath (JSON, UTF-8) via temporary PowerShell script; merge if exists
  ; Important: we assign the raw path to the PS object and rely on ConvertTo-Json to escape backslashes
  ;
  ; И19: КАНОНИЧЕСКОЕ РАСПОЛОЖЕНИЕ КОНФИГА — %APPDATA%\ChillHub\config.json.
  ;
  ; Здесь стояло 'LocalApplicationData', то есть установщик писал конфиг в
  ; %LOCALAPPDATA%\ChillHub — а это, по умолчанию, КАТАЛОГ УСТАНОВКИ лаунчера
  ; (см. ${INSTALL_DIR} выше). Лаунчер же читает конфиг из %APPDATA%\ChillHub
  ; (launcher/ChillHub/Core/Config.cs: ConfigPath), а из LOCALAPPDATA только
  ; ОДИН РАЗ мигрирует, и то лишь если целевого файла ещё нет.
  ;
  ; Последствия были: при переустановке поверх существующего профиля выбранная
  ; пользователем папка для игр молча игнорировалась — миграция не срабатывала,
  ; потому что %APPDATA%\ChillHub\config.json уже существовал. Плюс конфиг
  ; оказывался внутри каталога установки, откуда он и попадал когда-то в пакет
  ; сборки (тот самый цикл самообновления, ради которого он вынесен в APPDATA).
  ;
  ; Теперь установщик пишет туда же, откуда лаунчер читает, и делает это
  ; независимо от того, какой каталог установки выбран.
  InitPluginsDir
  FileOpen $3 "$PLUGINSDIR\write-config.ps1" w
  FileWrite $3 "param([string]$${GamesDir})$\r$\n"
  FileWrite $3 "$${dir} = [Environment]::GetFolderPath('ApplicationData')$\r$\n"
  FileWrite $3 "$${app} = Join-Path $${dir} 'ChillHub'$\r$\n"
  FileWrite $3 "if (-not (Test-Path $${app})) { New-Item -ItemType Directory -Path $${app} | Out-Null }$\r$\n"
  FileWrite $3 "$${cfg} = Join-Path $${app} 'config.json'$\r$\n"
  FileWrite $3 "$${data} = @{}$\r$\n"
  FileWrite $3 "if (Test-Path $${cfg}) { try { $${data} = Get-Content -Raw -LiteralPath $${cfg} | ConvertFrom-Json -ErrorAction Stop } catch { $${data} = @{} } }$\r$\n"
  FileWrite $3 "$${data}.GamesPath = $${GamesDir}  # raw path; ConvertTo-Json will write \\ in JSON$\r$\n"
  FileWrite $3 "$${json} = ($${data} | ConvertTo-Json -Depth 5)$\r$\n"
  FileWrite $3 "[IO.File]::WriteAllText($${cfg}, $${json}, [Text.UTF8Encoding]::new($${false}))$\r$\n"
  FileClose $3
  ExecWait '"powershell" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\write-config.ps1" -GamesDir "$GAMES_DIR"'

  ; Write launcher.version with exact content (no newline)
  FileOpen $4 "$INSTDIR\launcher.version" w
  FileWrite $4 "${APP_VERSION}"
  FileClose $4

  ; Prepare prerequisite installers in $PLUGINSDIR for optional install on Finish
  SetOutPath "$PLUGINSDIR\Redist"
  ; Use non-fatal includes so build continues if prereqs are not present
  ; NSIS will emit a warning if the file is missing and continue
  File /nonfatal "Redist\${PREREQ_WEBVIEW2}"
  File /nonfatal "Redist\${PREREQ_DOTNET}"

SectionEnd

; ============================================================================
; И18: ПРОВЕРКА ПУТИ ПЕРЕД РЕКУРСИВНЫМ УДАЛЕНИЕМ
; ============================================================================
; $Un_GamesDir читается из HKCU (${APP_REG}\GamesDir), куда он попадает из
; СВОБОДНОГО ТЕКСТОВОГО ПОЛЯ на странице выбора папки для игр. Единственной
; проверкой перед `RMDir /r` была непустая строка. Пользователь, указавший при
; установке "D:\", получал при удалении с галочкой рекурсивное стирание всего
; диска D. Ключ реестра пользовательский, то есть значение может быть и
; отредактировано вручную.
;
; un.SafeRmDir отклоняет:
;   * пустой путь;
;   * относительный путь (не вида X:\... и не UNC) — RMDir /r от относительного
;     пути отсчитывается от текущего каталога и вообще непредсказуем;
;   * корень диска ("D:", "D:\") и корень UNC-шары;
;   * системные каталоги и их родителей (Windows, Program Files, Users,
;     профиль пользователя, рабочий стол, каталоги данных приложений);
;   * путь, совпадающий с каталогом установки или лежащий выше него.
;
; Отказ ГРОМКИЙ: пользователю показывается, какой именно путь отклонён и что
; удалить его можно вручную. Молчаливый пропуск здесь хуже — человек будет
; думать, что место освободилось.
;
; Отклонить, если проверяемый путь РАВЕН защищённому каталогу или является его
; РОДИТЕЛЕМ (удаление C:\Users снесло бы профиль). Сравнение в NSIS
; регистронезависимое, что здесь и требуется.
;
; Проверка «родитель» требует, чтобы следующим символом был разделитель:
; иначе путь D:\Games ложно совпал бы с защищённым D:\GamesData.
!macro _UN_REJECT_IF Protected Reason
  ${If} $R1 == "ok"
  ${AndIf} "${Protected}" != ""
    ${If} "$R0" == "${Protected}"
      StrCpy $R1 "${Reason}"
    ${Else}
      StrLen $R4 "$R0"
      IntOp $R4 $R4 + 1
      StrCpy $R3 "${Protected}" $R4
      ${If} "$R3" == "$R0\"
        StrCpy $R1 "внутри него лежит защищённый каталог (${Reason})"
      ${EndIf}
    ${EndIf}
  ${EndIf}
!macroend

; Вход: путь на вершине стека. Выход: путь обратно на стеке, вердикт в $R1
; ("ok" либо причина отказа человеческим текстом).
;
; Логика намеренно написана на LogicLib (${If}), а не на StrCmp/IntCmp с
; относительными переходами: в NSIS смещения вида `Goto +3` пересчитываются
; вручную и молча ломаются при любой правке — в коде, который делает RMDir /r,
; такая хрупкость недопустима.
Function un.SafeRmDir
  Exch $R0
  Push $R2
  Push $R3
  Push $R4

  StrCpy $R1 "ok"

  ; Завершающий слэш убираем, чтобы "D:\" и "D:" проверялись одинаково.
  StrCpy $R2 $R0 1 -1
  ${If} $R2 == "\"
    StrCpy $R0 $R0 -1
  ${EndIf}

  StrLen $R3 $R0

  ${If} $R0 == ""
    StrCpy $R1 "путь пуст"
  ${ElseIf} $R3 < 4
    ; "D:", "D:\", "\\s" — корень диска или огрызок. Осмысленный каталог для
    ; игр короче четырёх символов быть не может.
    StrCpy $R1 "путь слишком короткий, это корень диска или его огрызок"
  ${Else}
    ; Требуем строго локальный абсолютный путь вида "X:\...".
    ; UNC (\\server\share) отвергается намеренно: рекурсивно стирать сетевую
    ; шару из деинсталлятора — это удаление чужих данных, а не своих.
    StrCpy $R2 $R0 2 1
    ${If} $R2 != ":\"
      StrCpy $R1 "путь не является локальным абсолютным (ожидается вида D:\Games\ChillHub)"
    ${EndIf}
  ${EndIf}

  ; Системные и пользовательские каталоги — сам путь либо его родитель.
  !insertmacro _UN_REJECT_IF "$WINDIR"          "системный каталог Windows"
  !insertmacro _UN_REJECT_IF "$SYSDIR"          "системный каталог Windows"
  !insertmacro _UN_REJECT_IF "$PROGRAMFILES"    "Program Files"
  !insertmacro _UN_REJECT_IF "$PROGRAMFILES64"  "Program Files"
  !insertmacro _UN_REJECT_IF "$COMMONFILES"     "Common Files"
  !insertmacro _UN_REJECT_IF "$PROFILE"         "профиль пользователя"
  !insertmacro _UN_REJECT_IF "$DESKTOP"         "рабочий стол"
  !insertmacro _UN_REJECT_IF "$DOCUMENTS"       "папка документов"
  !insertmacro _UN_REJECT_IF "$APPDATA"         "каталог данных приложений"
  !insertmacro _UN_REJECT_IF "$LOCALAPPDATA"    "локальный каталог данных приложений"
  !insertmacro _UN_REJECT_IF "$TEMP"            "временный каталог"
  !insertmacro _UN_REJECT_IF "$INSTDIR"         "каталог установки лаунчера"

  Pop $R4
  Pop $R3
  Pop $R2
  Exch $R0
FunctionEnd

; Macro to optionally delete games folder on uninstall
!macro _DELETE_GAMES_IF_CHECKED
  ; Use cached state from un.SelectDeleteGames_Leave, because the page controls are destroyed before this section runs
  ${If} $DeleteGames_State == 1
    ${If} $Un_GamesDir == ""
      MessageBox MB_ICONSTOP "Путь к папке с играми пуст. Удаление отменено."
    ${Else}
      ; И18: проверяем путь ДО рекурсивного удаления.
      Push "$Un_GamesDir"
      Call un.SafeRmDir
      Pop $R0
      ${If} $R1 != "ok"
        MessageBox MB_ICONSTOP "Папка с играми НЕ удалена: $R1.$\r$\nПуть: $Un_GamesDir$\r$\nЕсли этот путь действительно нужно удалить, сделайте это вручную."
      ${Else}
        IfFileExists "$Un_GamesDir\*.*" 0 +3
          RMDir /r "$Un_GamesDir"
          Goto +2
        MessageBox MB_ICONINFORMATION "Папка с играми не найдена: $Un_GamesDir"
      ${EndIf}
    ${EndIf}
  ${Else}
    MessageBox MB_ICONINFORMATION "Папка с играми не была удалена. Вы можете удалить её вручную: $Un_GamesDir"
  ${EndIf}
!macroend

Section "Uninstall"
  ; Remove Start Menu shortcuts (per-user)
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"

  ; Remove Desktop shortcut
  Delete "$DESKTOP\${APP_NAME}.lnk"

  ; И19: удаление действительно УДАЛЯЕТ каталог установки.
  ;
  ; Здесь была пляска «сохранить $INSTDIR\config.json во временный файл ->
  ; удалить каталог -> СОЗДАТЬ КАТАЛОГ ЗАНОВО и положить конфиг обратно».
  ; После «полного удаления» на диске оставался каталог с config.json внутри,
  ; и пользователь, выбравший удаление, получал не то, что просил.
  ;
  ; Вдобавок пляска была бессмысленной: пользовательский конфиг живёт в
  ; %APPDATA%\ChillHub\config.json (см. Config.cs и запись конфига в секции
  ; установки выше), а не в каталоге установки. Условие IfFileExists почти
  ; всегда было ложным, и весь блок сводился к воссозданию пустого каталога.
  ;
  ; Настройки пользователя в %APPDATA% НЕ трогаем намеренно: они переживают
  ; переустановку, это ожидаемое поведение. Полная очистка — ручная, и это
  ; сознательный выбор: молча стирать настройки при удалении приложения хуже,
  ; чем оставить небольшой JSON.
  RMDir /r "$INSTDIR"

  ; Remove registry uninstall entry
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "${APP_REG}"

  ; Ask-delete games folder per user's choice (from custom page)
  !insertmacro _DELETE_GAMES_IF_CHECKED

SectionEnd

; =========================
; Helper: default games dir detection
; =========================
Function .onInit
  ; Ensure per-user shell variables context
  SetShellVarContext current
  StrCpy $GAMES_DIR "C:\Games\ChillHub"
  IfFileExists "D:\*.*" 0 +2
    StrCpy $GAMES_DIR "D:\Games\ChillHub"
FunctionEnd

; =========================
; Custom Page: Select Games Directory
; =========================
Function SelectGamesDir_Create
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 24 "Выберите папку для хранения игр, загруженных в лаунчере ChillHub"
  Pop $GamesDir_Label

  ${NSD_CreateText} 0 28 80% 26 "$GAMES_DIR"
  Pop $GamesDir_Edit

  ${NSD_CreateBrowseButton} 82% 26 18% 26 "Обзор..."
  Pop $GamesDir_Browse
  ${NSD_OnClick} $GamesDir_Browse SelectGamesDir_Browse
 
  nsDialogs::Show
FunctionEnd

Function SelectGamesDir_Browse
  nsDialogs::SelectFolderDialog "Выберите папку для игр" "$GAMES_DIR"
  Pop $1
  ${If} $1 != ""
    StrCpy $GAMES_DIR $1
    ${NSD_SetText} $GamesDir_Edit $GAMES_DIR
  ${EndIf}
FunctionEnd

Function SelectGamesDir_Leave
  ${NSD_GetText} $GamesDir_Edit $GAMES_DIR
  ${If} $GAMES_DIR == ""
    MessageBox MB_ICONEXCLAMATION "Укажите папку для установки игр."
    Abort
  ${EndIf}
  ; Create if missing
  IfFileExists "$GAMES_DIR" +2 0
    CreateDirectory "$GAMES_DIR"
FunctionEnd

; =========================
; Finish page actions
; =========================
Function RunAppAfterInstall
  ; If user ticked "Запустить ChillHub", set a flag; actual launch may be deferred
  StrCpy $LaunchAfterFlag 1
FunctionEnd

Function InstallPrereqs
  ; Offer to run .NET Desktop Runtime 8 and WebView2 installers (interactive UI)
  ; Run sequentially and only after both finish optionally launch the app if requested
  StrCpy $PrereqsRan 1
  StrCpy $0 "$PLUGINSDIR\Redist\${PREREQ_DOTNET}"
  StrCpy $1 "$PLUGINSDIR\Redist\${PREREQ_WEBVIEW2}"
  ; .NET Runtime
  IfFileExists "$0" 0 +2
    ExecWait '"$0"'
  ; WebView2
  IfFileExists "$1" 0 +2
    ExecWait '"$1"'
  ; If user asked to run app, do it now after prereqs
  StrCmp $LaunchAfterFlag 1 0 +2
    ExecShell "open" "$INSTDIR\${APP_EXE}"
FunctionEnd

Function .onInstSuccess
  ; If only "Run app" was selected (without prereqs), launch now
  StrCmp $LaunchAfterFlag 1 0 done
  StrCmp $PrereqsRan 1 done 0
  ExecShell "open" "$INSTDIR\${APP_EXE}"
done:
FunctionEnd

; =========================
; Uninstall: ask to delete games directory
; =========================
Function un.onInit
  ; Load games dir from registry; fallback to default
  ReadRegStr $Un_GamesDir HKCU "${APP_REG}" "GamesDir"
  ${If} $Un_GamesDir == ""
    StrCpy $Un_GamesDir "C:\Games\ChillHub"
    IfFileExists "D:\*.*" 0 +2
      StrCpy $Un_GamesDir "D:\Games\ChillHub"
  ${EndIf}
FunctionEnd

Function un.SelectDeleteGames_Create
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ; И27: перенос строки в NSIS — это $\r$\n, а не \r\n. С обратными слэшами
  ; пользователь видел в диалоге удаления буквальные символы:
  ;   «Удалить папку с играми?\r\nПапка: D:\Games\ChillHub»
  ${NSD_CreateLabel} 0 0 100% 40 "Удалить папку с играми?$\r$\nПапка: $Un_GamesDir"
  Pop $1
  ${NSD_CreateCheckbox} 0 46 100% 18 "Удалить папку с играми (безвозвратно)"
  Pop $DeleteGames_Check
  nsDialogs::Show
FunctionEnd

Function un.SelectDeleteGames_Leave
  ; Cache checkbox state because UI controls will be destroyed before uninstall section runs
  ${NSD_GetState} $DeleteGames_Check $2
  ${If} $2 == ${BST_CHECKED}
    StrCpy $DeleteGames_State 1
  ${Else}
    StrCpy $DeleteGames_State 0
  ${EndIf}
FunctionEnd
