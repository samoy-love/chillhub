; NSIS installer script for Chill Hub (per-user install)
; Encoding: UTF-8

Unicode true
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
; Разбор командной строки в ДЕИНСТАЛЛЯТОРЕ. У NSIS раздельные пространства кода
; для установщика и деинсталлятора, поэтому функции FileFunc нужно отдельно
; попросить сгенерировать un.-копии — иначе ${un.GetOptions} просто не
; существует (см. un.onInit: ключи /DELETEGAMES и /DELETESETTINGS).
!insertmacro un.GetParameters
!insertmacro un.GetOptions
; ${VersionCompare} — сравнение версий для защиты от отката (см. .onInit).
!include "WordFunc.nsh"

; ИМЯ ДЛЯ ГЛАЗ и ИМЯ ДЛЯ ПУТЕЙ — это разные вещи.
;
; APP_TITLE видит пользователь: заголовок установщика, ярлыки, строка в списке
; установленных программ, тексты сообщений. APP_NAME остаётся без пробела и
; ходит по файловой системе и реестру ($APPDATA\ChillHub, ключи установки).
; Разделены они не для красоты: переименование каталога данных отрезало бы
; конфиг и логи у всех, кто уже установил лаунчер.
!define APP_TITLE "Chill Hub"
!define APP_NAME "ChillHub"
!define COMPANY_NAME "Chill Hub"
!define APP_EXE "ChillHub.exe"
; Ярлыки прежних версий назывались по APP_NAME. Если их не убрать при
; установке, на рабочем столе и в «Пуске» останется вторая, устаревшая пара.
!define LEGACY_SHORTCUT_NAME "ChillHub"
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
; Пути в реестре — с ОДИНАРНЫМ разделителем.
;
; Здесь стояло "Software\\Microsoft\\..." по привычке к языкам, где обратный
; слэш — escape. В NSIS он им не является, поэтому в API уезжала строка с
; задвоенными разделителями. Работало это лишь потому, что реестр их
; схлопывает, — но ровно та же привычка уже испортила UninstallString, где
; схлопывать было некому (см. комментарий в секции Install).
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ChillHub"
!define APP_REG "Software\ChillHub\Install"

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
  !error "APP_VERSION is not defined. Build through scripts/build-installer.ps1 (it passes /DAPP_VERSION=...), or pass it by hand: makensis /DAPP_VERSION=1.2.3 /DAPP_VERSION_NUMERIC=1.2.3.0 /DPAYLOAD_DIR=... installer.nsi"
!endif

; APP_VERSION_NUMERIC — та же версия в виде, который требует ресурс версии
; Windows: ровно четыре числовых компонента. ${APP_VERSION} для этого не
; годится — релиз-кандидат вида "1.2.3-rc1" ресурс версии не принимает, а
; сборка проверки в CI и вовсе называется "0.0.0-ci".
;
; Приводит к нужному виду scripts/build-installer.ps1 (там же, где считается
; -p:FileVersion для самой сборки), и передаёт сюда. Дефолта нет по той же
; причине, что и у APP_VERSION: молчаливая заглушка означала бы установщик,
; который в свойствах файла показывает не свою версию.
!ifndef APP_VERSION_NUMERIC
  !error "APP_VERSION_NUMERIC is not defined (ожидается вид 1.2.3.0). Собирайте через scripts/build-installer.ps1 — он передаёт /DAPP_VERSION_NUMERIC=..."
!endif

; Ресурс версии самого ChillHub-Setup.exe.
;
; Его не было вовсе: в свойствах скачанного файла все поля пустые, и понять,
; какой это установщик, не запуская его, было нельзя. Отдельно это важно для
; подписи — репутационные механизмы Windows (SmartScreen) и антивирусы читают
; издателя и версию именно отсюда.
VIProductVersion "${APP_VERSION_NUMERIC}"
VIAddVersionKey "ProductName" "${APP_TITLE}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "CompanyName" "${COMPANY_NAME}"
VIAddVersionKey "LegalCopyright" "${COMPANY_NAME}"
VIAddVersionKey "FileDescription" "Установщик ${APP_TITLE}"

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
;
; WEBVIEW2 — ЭТО BOOTSTRAPPER (~2 МБ), А НЕ ОФЛАЙН-ИНСТАЛЛЯТОР (~183 МБ).
; Раньше в установщик зашивался полный офлайн-пакет, и он один давал три
; четверти веса дистрибутива. При этом WebView2 предустановлен в Windows 11 и
; давно разъехался на Windows 10 вместе с Edge, то есть почти всем этот пакет
; был не нужен. Bootstrapper проверяет наличие рантайма и качает его только
; тем, у кого его действительно нет.
;
; Инсталлятор .NET отсюда убран: лаунчер публикуется self-contained, рантайм
; едет внутри сборки, и доустанавливать нечего.
!define PREREQ_WEBVIEW2 "MicrosoftEdgeWebview2Setup.exe"

; Ключ, по которому Edge регистрирует установленный WebView2 Runtime.
; Он одинаков для машинной и пользовательской установки, различается только улей.
!define WEBVIEW2_CLIENT_KEY "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

Var GAMES_DIR
Var GamesDir_Edit
Var GamesDir_Browse
Var GamesDir_Label
Var DeleteGames_Check
Var Un_GamesDir
Var DeleteGames_State
Var LaunchAfterFlag
Var PrereqsRan
Var WebView2Present
Var DesktopShortcut_Check
Var DesktopShortcut_State
Var DeleteSettings_Check
Var DeleteSettings_State

; Адрес проекта: он же уезжает в «Установку и удаление программ» (URLInfoAbout,
; HelpLink) — из списка программ должно быть куда пойти за помощью.
!define APP_URL "https://launcher.samoy.love"

; Output installer
Name "${APP_TITLE}"
OutFile "generated_downloads\ChillHub-Setup.exe"

; Per-user installation (no admin)
RequestExecutionLevel user

; Compression
;
; SOLID: полезная нагрузка — self-contained сборка .NET, то есть несколько тысяч
; файлов, из которых сотни библиотек рантайма похожи друг на друга. Без /SOLID
; NSIS жмёт каждый файл ОТДЕЛЬНЫМ потоком LZMA: словарь сбрасывается на каждом
; файле, и повторы между файлами не находятся вовсе. Solid-режим жмёт всё одним
; потоком — на этой нагрузке это единственная настройка сжатия, которая
; действительно что-то меняет.
;
; Словарь поднят с 16 до 64 МБ: смысл solid-потока в том, чтобы совпадения
; находились НА РАССТОЯНИИ, а маленький словарь ровно это и обрезает. Память
; нужна только машине, которая собирает (примерно десятикратно от словаря);
; распаковка у пользователя от размера словаря не зависит.
SetCompress auto
SetCompressor /SOLID lzma
SetCompressorDictSize 64
SetDatablockOptimize on

; MUI options (simple modern touches)
!define MUI_ABORTWARNING
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT

; Finish page settings: show prerequisites first, then run app
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Доустановить WebView2 (нужен для показа новостей)"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION InstallPrereqs
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Запустить ${APP_TITLE}"
!define MUI_FINISHPAGE_RUN_FUNCTION RunAppAfterInstall

; Page sequence
!insertmacro MUI_PAGE_WELCOME
; Каталог установки проверяется на запись ДО того, как начнётся распаковка
; (см. DirectoryLeave). Определение стоит вплотную к своей странице намеренно:
; MUI применяет MUI_PAGE_CUSTOMFUNCTION_* к БЛИЖАЙШЕЙ следующей странице и тут
; же их снимает.
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE DirectoryLeave
!insertmacro MUI_PAGE_DIRECTORY
Page Custom SelectGamesDir_Create SelectGamesDir_Leave
!insertmacro MUI_PAGE_INSTFILES
; Показ финальной страницы — момент, когда галочку WebView2 можно спрятать
; (см. FinishPageShow): страница уже создана, но ещё не показана.
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FinishPageShow
!insertmacro MUI_PAGE_FINISH

; Uninstall pages
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage Custom un.SelectDeleteGames_Create un.SelectDeleteGames_Leave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; ЯЗЫК ОДИН — РУССКИЙ.
;
; Здесь подключались оба, Russian и English, и это давало не выбор, а мешанину:
; языкового диалога нет (MUI_LANGDLL_DISPLAY не вставлен), поэтому NSIS сам
; выбирает язык по локали системы. На английской Windows пользователь получал
; английские кнопки MUI — и русские тексты на страницах выбора папки для игр и
; удаления, потому что все они здесь захардкожены по-русски. Сам лаунчер тоже
; русскоязычный.
;
; Один язык честнее двух с половиной. Если появится настоящая локализация,
; строки кастомных страниц надо будет вынести в LangString, а сюда вернуть
; вторую MUI_LANGUAGE — вместе, а не по отдельности.
!insertmacro MUI_LANGUAGE "Russian"

; Default installation directory
InstallDir "${INSTALL_DIR}"

; ПОВТОРНАЯ УСТАНОВКА ПРОДОЛЖАЕТ ПРЕДЫДУЩУЮ, А НЕ НАЧИНАЕТ ВТОРУЮ.
;
; Подхвата предыдущего каталога не было: и при обновлении предлагался
; $LOCALAPPDATA\ChillHub, как при первой установке. Кто ставил в D:\ChillHub,
; при обновлении получал ВТОРУЮ копию — а запись в списке программ одна, и она
; перезаписывалась на новый путь. Старая установка оставалась на диске
; навсегда, удалить её штатно было уже нечем.
;
; Именно InstallDirRegKey, а не чтение реестра в .onInit: ключ командной строки
; /D=<путь> (тихая установка) отменяет InstallDirRegKey сам, тогда как StrCpy в
; .onInit выполняется ПОСЛЕ разбора /D и молча его затирает. Так и вышло с
; первой версией этой правки — тихая установка ставила куда угодно, только не
; туда, куда просили; поймала это smoke-проверка (scripts/ci/smoke-installer.ps1).
InstallDirRegKey HKCU "${UNINST_KEY}" "InstallLocation"

; ============================================================================
; УСТАНОВКА И УДАЛЕНИЕ ПОВЕРХ ЗАПУЩЕННОГО ЛАУНЧЕРА
; ============================================================================
; Проверки не было вообще. Установка поверх работающего ChillHub упиралась в
; занятый ChillHub.exe уже на середине распаковки, и NSIS показывал системный
; диалог «не удаётся записать файл» — посреди процесса, без объяснения причины
; и с половиной новых файлов на диске. Сценарий не экзотический, а самый
; частый: человек скачивает свежую сборку с сайта, не закрывая лаунчер.
; Отдельно коварно то, что лаунчер умеет сворачиваться в трей (MinimizeToTray),
; то есть «закрытым» он выглядит и будучи запущенным.
;
; То же самое верно для удаления: RMDir /r по каталогу с работающим процессом
; молча оставляет .exe и всё, что рядом с ним занято, — запись в списке
; программ при этом исчезает, а каталог на диске остаётся.
;
; Проверяем не список процессов, а ровно то, что нам мешает: возможность
; открыть файл на запись в монопольном режиме. Так ловится и сам лаунчер, и
; любой другой держатель файла. Плагинов это не требует: System.dll входит в
; поставку NSIS.
;
; Макрос порождает две копии функции — для установщика и для деинсталлятора: в
; NSIS у них раздельные пространства кода, и вызывать одну и ту же функцию из
; обоих нельзя.
!define _CH_GENERIC_WRITE 0x40000000
!define _CH_OPEN_EXISTING 3
!macro _ENSURE_APP_CLOSED_FN UN
Function ${UN}EnsureAppClosed
  Push $0
retry:
  IfFileExists "$INSTDIR\${APP_EXE}" 0 done
  ; CreateFileW(путь, GENERIC_WRITE, dwShareMode=0, NULL, OPEN_EXISTING, 0, NULL)
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\${APP_EXE}", i ${_CH_GENERIC_WRITE}, i 0, p 0, i ${_CH_OPEN_EXISTING}, i 0, p 0) p .r0'
  ${If} $0 = -1
    ; /SD IDCANCEL — тихий режим не имеет права ЖДАТЬ человека у диалога. Без
    ; /SD NSIS показывает окно даже установщику, запущенному с /S: тихая
    ; установка на занятых файлах не падала бы, а висела до конца таймаута.
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
      "Файлы ${APP_TITLE} сейчас заняты — похоже, лаунчер запущен.$\r$\n$\r$\nЗакройте ${APP_TITLE} (проверьте значок в области уведомлений рядом с часами) и нажмите «Повторить»." \
      /SD IDCANCEL IDRETRY retry
    Abort "Прервано: ${APP_TITLE} не был закрыт."
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p r0)'
done:
  Pop $0
FunctionEnd
!macroend
!insertmacro _ENSURE_APP_CLOSED_FN ""
!insertmacro _ENSURE_APP_CLOSED_FN "un."

; ------------------------
; Sections
; ------------------------
Section "Install" SecInstall
  ; Занятые файлы — до первой записи на диск, а не посреди распаковки.
  Call EnsureAppClosed

  ; Место на диске — тоже до первой записи. Тихая установка страницу выбора
  ; каталога не проходит, поэтому проверка нужна и здесь, а не только там.
  Call CheckDiskSpace

  ; Ensure install dir
  CreateDirectory "$INSTDIR"

  ; Устаревшие файлы прошлой версии убираются ДО распаковки.
  Call CleanPreviousInstall

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
  ; Ярлыки прежнего имени убираем до создания новых: иначе в «Пуске» и на
  ; рабочем столе останется по два ярлыка на один и тот же лаунчер.
  Delete "$SMPROGRAMS\${LEGACY_SHORTCUT_NAME}\${LEGACY_SHORTCUT_NAME}.lnk"
  Delete "$SMPROGRAMS\${LEGACY_SHORTCUT_NAME}\Uninstall ${LEGACY_SHORTCUT_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${LEGACY_SHORTCUT_NAME}"
  CreateDirectory "$SMPROGRAMS\${APP_TITLE}"
  CreateShortCut "$SMPROGRAMS\${APP_TITLE}\${APP_TITLE}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortCut "$SMPROGRAMS\${APP_TITLE}\Uninstall ${APP_TITLE}.lnk" "$INSTDIR\Uninstall.exe"

  ; Ярлык на рабочем столе — ПО ВЫБОРУ. Раньше он создавался всегда и никого
  ; не спрашивал; рабочий стол — не место, куда программа въезжает молча.
  ; В тихом режиме галочка считается отмеченной: поведение по умолчанию не
  ; меняется для тех, кто ставит скриптом.
  ${If} $DesktopShortcut_State == 1
    Delete "$DESKTOP\${LEGACY_SHORTCUT_NAME}.lnk"
    CreateShortCut "$DESKTOP\${APP_TITLE}.lnk" "$INSTDIR\${APP_EXE}"
  ${EndIf}

  ; Uninstall registry (per-user)
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_TITLE}"
  ; Один слэш, а не два: NSIS не обрабатывает \\ как escape, и в реестр
  ; уезжала строка с задвоенным разделителем.
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${COMPANY_NAME}"
  ; Из списка программ должно быть куда пойти: без этих двух полей у записи
  ; нет ни ссылки на сайт, ни ссылки на поддержку.
  WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "${APP_URL}"
  WriteRegStr HKCU "${UNINST_KEY}" "HelpLink" "${APP_URL}"

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

  ; Дата установки: без неё в списке программ пустая колонка «Дата установки»,
  ; по которой этот список чаще всего и сортируют. Формат — YYYYMMDD, как ждёт
  ; Windows. ${GetTime} отдаёт день/месяц/год отдельными значениями, и месяц с
  ; днём приходят уже с ведущим нулём.
  ${GetTime} "" "L" $1 $2 $3 $4 $5 $6 $7
  WriteRegStr HKCU "${UNINST_KEY}" "InstallDate" "$3$2$1"

  ; EstimatedSize (в КиБ) считается ПОСЛЕ распаковки файлов — иначе считать
  ; было бы нечего. Без него Windows показывает пустой размер.
  ;
  ; Считается ЛАУНЧЕР ВМЕСТЕ С ПАПКОЙ ДЛЯ ИГР: ради этого числа в список
  ; программ и заходят. Сам лаунчер весит пару сотен мегабайт и среди прочих
  ; программ не выделяется ничем, а игры — десятки гигабайт; показав только
  ; каталог установки, мы спрятали бы ровно тот объём, который человек ищет.
  ; При первой установке игр ещё нет и слагаемое нулевое, при переустановке
  ; поверх — уже нет. Дальше это число поддерживает сам лаунчер на каждом
  ; запуске (Core/Shell/InstalledAppsEntry.cs): Windows EstimatedSize никогда
  ; не пересчитывает, а игры приезжают и уезжают после установки.
  ;
  ; Папка для игр, указанная ВНУТРИ каталога установки, посчиталась бы дважды,
  ; поэтому складываем только когда она лежит отдельно. Сравнение грубое (равен
  ; ли префикс), и этого здесь достаточно: точность до вложенных путей —
  ; в лаунчере, у которого для этого есть настоящая работа с путями.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  StrLen $1 "$INSTDIR"
  StrCpy $2 "$GAMES_DIR" $1
  ${If} "$2" != "$INSTDIR"
  ${AndIf} ${FileExists} "$GAMES_DIR\*.*"
    ; Каталог обязан существовать: ${GetSize} по отсутствующему пути возвращает
    ; не ноль, а пустую строку, и IntOp прибавил бы к размеру мусор.
    ${GetSize} "$GAMES_DIR" "/S=0K" $1 $2 $3
    IntOp $0 $0 + $1
  ${EndIf}
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

  ; PowerShell зовётся ПО ПОЛНОМУ ПУТИ из $SYSDIR, а не по имени.
  ;
  ; Было '"powershell" ...', то есть поиск по PATH. Установщик запускал то, что
  ; первым попадётся в пользовательском PATH, — а PATH пользователя правится
  ; без каких-либо прав. Для процесса, который пишет файлы в профиль, это
  ; лишний и ничем не оправданный способ подсунуть свой исполняемый файл.
  StrCpy $6 "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe"
  IfFileExists "$6" +2 0
    StrCpy $6 "powershell"

  ; РЕЗУЛЬТАТ ПРОВЕРЯЕТСЯ.
  ;
  ; Код возврата не смотрели вовсе. Если PowerShell не запустился (политика,
  ; вырезанный компонент, антивирус), выбранная пользователем папка для игр
  ; просто НЕ СОХРАНЯЛАСЬ, а установка рапортовала об успехе: лаунчер потом
  ; молча качал игры в путь по умолчанию, и виноватым выглядел он.
  ;
  ; Записать конфиг напрямую из NSIS нельзя, и это осознанно: config.json несёт
  ; и остальные настройки пользователя (тема, лимит скорости, число потоков), а
  ; переустановка не должна их стирать. Слияние требует разбора JSON — отсюда и
  ; внешний процесс. Раз уж он есть, его исход обязан быть проверен.
  ClearErrors
  ExecWait '"$6" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\write-config.ps1" -GamesDir "$GAMES_DIR"' $7
  ${If} ${Errors}
  ${OrIf} $7 != 0
    MessageBox MB_ICONEXCLAMATION \
      "Не удалось сохранить папку для игр в настройках (код: $7).$\r$\n$\r$\nСам ${APP_TITLE} установлен и работает — укажите папку вручную при первом запуске, в настройках.$\r$\nВыбранный путь: $GAMES_DIR" \
      /SD IDOK
  ${EndIf}

  ; Write launcher.version with exact content (no newline)
  FileOpen $4 "$INSTDIR\launcher.version" w
  FileWrite $4 "${APP_VERSION}"
  FileClose $4

  ; Prepare prerequisite installers in $PLUGINSDIR for optional install on Finish
  SetOutPath "$PLUGINSDIR\Redist"
  ; Use non-fatal includes so build continues if prereqs are not present
  ; NSIS will emit a warning if the file is missing and continue
  File /nonfatal "Redist\${PREREQ_WEBVIEW2}"

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
  ; У ВСЕХ окон здесь есть /SD.
  ;
  ; Без него тихое удаление (uninstaller /S) ВИСЛО: NSIS показывает MessageBox и
  ; в тихом режиме, если ему не сказали, что отвечать. А последняя ветка ниже
  ; срабатывает как раз всегда, когда галочку не ставили, — то есть при любом
  ; тихом удалении. Ловится это только автоматической проверкой (её и не было),
  ; потому что руками удаление всегда запускают из окна.
  ;
  ; Ответы выбраны так, чтобы тихий режим НИЧЕГО НЕ УДАЛЯЛ сверх каталога
  ; установки: папка с играми — пользовательские данные, и решение о ней
  ; принимает человек, а не отсутствие ответа.
  ;
  ; Use cached state from un.SelectDeleteGames_Leave, because the page controls are destroyed before this section runs
  ${If} $DeleteGames_State == 1
    ${If} $Un_GamesDir == ""
      MessageBox MB_ICONSTOP "Путь к папке с играми пуст. Удаление отменено." /SD IDOK
    ${Else}
      ; И18: проверяем путь ДО рекурсивного удаления.
      Push "$Un_GamesDir"
      Call un.SafeRmDir
      Pop $R0
      ${If} $R1 != "ok"
        MessageBox MB_ICONSTOP "Папка с играми НЕ удалена: $R1.$\r$\nПуть: $Un_GamesDir$\r$\nЕсли этот путь действительно нужно удалить, сделайте это вручную." /SD IDOK
      ${Else}
        IfFileExists "$Un_GamesDir\*.*" 0 +3
          RMDir /r "$Un_GamesDir"
          Goto +2
        MessageBox MB_ICONINFORMATION "Папка с играми не найдена: $Un_GamesDir" /SD IDOK
      ${EndIf}
    ${EndIf}
  ${Else}
    MessageBox MB_ICONINFORMATION "Папка с играми не была удалена. Вы можете удалить её вручную: $Un_GamesDir" /SD IDOK
  ${EndIf}
!macroend

Section "Uninstall"
  ; Работающий лаунчер не даст удалить свой .exe: без этой проверки удаление
  ; «проходило» с занятыми файлами.
  Call un.EnsureAppClosed

  ; Remove Start Menu shortcuts (per-user)
  Delete "$SMPROGRAMS\${APP_TITLE}\${APP_TITLE}.lnk"
  Delete "$SMPROGRAMS\${APP_TITLE}\Uninstall ${APP_TITLE}.lnk"
  RMDir  "$SMPROGRAMS\${APP_TITLE}"
  ; Ярлыки версий до переименования: кто ставил старый билд и с тех пор не
  ; переустанавливался, тому удаление обязано убрать и их.
  Delete "$SMPROGRAMS\${LEGACY_SHORTCUT_NAME}\${LEGACY_SHORTCUT_NAME}.lnk"
  Delete "$SMPROGRAMS\${LEGACY_SHORTCUT_NAME}\Uninstall ${LEGACY_SHORTCUT_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${LEGACY_SHORTCUT_NAME}"

  ; Remove Desktop shortcut
  Delete "$DESKTOP\${APP_TITLE}.lnk"
  Delete "$DESKTOP\${LEGACY_SHORTCUT_NAME}.lnk"

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

  ; Настройки удаляются только по явной галочке (см. страницу удаления).
  ; Тихое удаление сюда не попадает: $DeleteSettings_State там остаётся нулём.
  ${If} $DeleteSettings_State == 1
    ; %APPDATA%\ChillHub — конфиг, очередь отчётов, каталог данных WebView2
    ; (Core/News/NewsWebViewStorage.cs кладёт его именно сюда, чтобы его не
    ; сносило самообновление).
    RMDir /r "$APPDATA\${APP_NAME}"

    ; %LOCALAPPDATA%\ChillHub — каталог установки ПО УМОЛЧАНИЮ, и при обычной
    ; установке его уже снёс RMDir /r "$INSTDIR" выше. Но кто указал свой путь
    ; (D:\ChillHub), у того здесь остаётся каталог прежней установки вместе со
    ; старым config.json, откуда лаунчер когда-то мигрировал настройки
    ; (Core/Config.cs: второй каталог ConfigStore). «Удалить настройки» обязано
    ; убирать и его — иначе после полного удаления на диске остаётся папка,
    ; о которой человек уже не помнит.
    ${If} "$INSTDIR" != "$LOCALAPPDATA\${APP_NAME}"
      RMDir /r "$LOCALAPPDATA\${APP_NAME}"
    ${EndIf}
  ${EndIf}

  ; Ask-delete games folder per user's choice (from custom page)
  !insertmacro _DELETE_GAMES_IF_CHECKED

SectionEnd

; =========================
; Helper: default games dir detection
; =========================
Function .onInit
  ; Ensure per-user shell variables context
  SetShellVarContext current

  ; Папка для игр — от предыдущей установки (каталог установки подхватывает
  ; InstallDirRegKey выше). Подставлялся C:\Games\ChillHub, и если пользователь
  ; не замечал подмены на странице выбора, установщик записывал этот путь в
  ; config.json поверх настоящего. Лаунчер после переустановки переставал видеть
  ; уже скачанные игры — они лежали там, куда он больше не смотрит.
  ReadRegStr $0 HKCU "${APP_REG}" "GamesDir"
  ${If} $0 != ""
    StrCpy $GAMES_DIR $0
  ${Else}
    StrCpy $GAMES_DIR "C:\Games\ChillHub"
    IfFileExists "D:\*.*" 0 +2
      StrCpy $GAMES_DIR "D:\Games\ChillHub"
  ${EndIf}

  ; Ключ командной строки перекрывает и реестр, и значение по умолчанию.
  ;
  ; Тихая установка умела задавать только каталог программы (/D=), а папку для
  ; игр брала из реестра или подставляла C:\Games\ChillHub — и молча писала её
  ; в config.json. То есть скриптом поставить лаунчер С НУЖНОЙ папкой для игр
  ; было нельзя вообще, а результат ещё и зависел от того, что осталось в
  ; реестре от прошлой установки.
  ;
  ;   ChillHub-Setup.exe /S /GAMESDIR=D:\Games /D=C:\ChillHub
  ;
  ; (/D=, как требует NSIS, остаётся последним и без кавычек.)
  ${GetParameters} $R0
  ${GetOptions} $R0 "/GAMESDIR=" $R1
  ${If} $R1 != ""
    StrCpy $GAMES_DIR $R1
  ${EndIf}

  ; Ярлык на рабочем столе по умолчанию создаётся — в тихом режиме страницы с
  ; галочкой никто не увидит, а менять поведение тихой установки этой правкой
  ; не хотелось.
  StrCpy $DesktopShortcut_State 1

  ; УСТАНОВКА СТАРОЙ ВЕРСИИ ПОВЕРХ НОВОЙ — С ПОДТВЕРЖДЕНИЕМ.
  ;
  ; Версии не сравнивались нигде. Запуск установщика полугодовой давности из
  ; папки «Загрузки» откатывал лаунчер молча: файлы старее, launcher.version
  ; перезаписан меньшим номером. Самообновление потом это вылечит, но человек
  ; какое-то время работает не с той сборкой и не знает об этом.
  ;
  ; Тихому режиму отвечаем «нет»: скриптовый откат версии почти наверняка
  ; ошибка, и совершать её молча — худший из вариантов.
  ReadRegStr $0 HKCU "${UNINST_KEY}" "DisplayVersion"
  ${If} $0 != ""
    ; 0 — равны, 1 — установленная новее, 2 — устанавливаемая новее.
    ${VersionCompare} "$0" "${APP_VERSION}" $1
    ${If} $1 == 1
      MessageBox MB_YESNO|MB_ICONEXCLAMATION \
        "Установлена версия $0, а этот установщик ставит ${APP_VERSION} — более старую.$\r$\n$\r$\nПродолжить и откатить ${APP_TITLE} до ${APP_VERSION}?" \
        /SD IDNO IDYES continue
      Abort
    ${EndIf}
  ${EndIf}
continue:

  ; Наличие WebView2 выясняется ЗДЕСЬ, а не в обработчике галочки на финальной
  ; странице: раньше проверка жила внутри InstallPrereqs, то есть срабатывала
  ; уже ПОСЛЕ клика. Пользователю Windows 11, где рантайм предустановлен,
  ; предлагали доустановить то, что у него есть, — и он либо тратил время, либо
  ; снимал галочку, гадая, не сломает ли этим новости.
  Call DetectWebView2
FunctionEnd

; ============================================================================
; МЕСТО НА ДИСКЕ
; ============================================================================
; Не проверялось. Страница выбора каталога у MUI показывает «требуется/
; доступно», но продолжить не мешает, а тихая установка эту страницу вообще не
; видит. На забитом диске распаковка 170 МБ падала на середине — уже без
; всяких диалогов, просто ошибкой записи, оставляя половину файлов.
;
; Размер берётся у самой секции (SectionGetSize, КиБ) — это ровно то, что
; посчитал компилятор по её File-командам, а не отдельно поддерживаемое число,
; которое разъедется с содержимым при первой же правке.
!define CH_INSTALL_SPARE_MB 30
Function CheckDiskSpace
  Push $0
  Push $1
  Push $2

  SectionGetSize ${SecInstall} $0
  ; КиБ -> МиБ, с запасом на временные файлы установки.
  IntOp $0 $0 / 1024
  IntOp $0 $0 + ${CH_INSTALL_SPARE_MB}

  ${GetRoot} "$INSTDIR" $1
  ${DriveSpace} "$1\" "/D=F /S=M" $2

  ; Пустой ответ = диск не опрошен (сетевой путь, съёмный носитель без
  ; носителя). Это не повод отказывать: пусть решает сама распаковка.
  ${If} $2 != ""
  ${AndIf} $2 < $0
    MessageBox MB_ICONSTOP \
      "На диске $1 недостаточно места.$\r$\n$\r$\nНужно примерно $0 МБ, свободно $2 МБ.$\r$\nОсвободите место или выберите другой диск." \
      /SD IDOK
    Pop $2
    Pop $1
    Pop $0
    Abort
  ${EndIf}

  Pop $2
  Pop $1
  Pop $0
FunctionEnd

; ============================================================================
; ЧИСТКА ПРЕДЫДУЩЕЙ УСТАНОВКИ
; ============================================================================
; `File /r` перезаписывает и добавляет, но НИКОГДА не удаляет. Файл, исчезнувший
; в новой версии, оставался в каталоге навсегда: при смене патча рантайма .NET
; часть библиотек в runtimes\ меняет имена, и рядом с новыми копились старые.
; Самообновление так не делает — у него манифест со списком удаления, — а
; установщик делал.
;
; Чистка включается ТОЛЬКО при наличии launcher.version: этот файл пишет сюда
; сам установщик, и он же доказывает, что каталог наш. Без такого доказательства
; рекурсивно удалять содержимое каталога, который пользователь выбрал руками,
; нельзя ни при каких обстоятельствах — он мог указать существующую папку.
;
; Список сохраняемых файлов обязан совпадать со списком /x у File выше; это
; сверяется тестом (server/internal/adminapi/builds/installersync_test.go),
; поэтому имена здесь написаны в открытую, а не собраны в макрос.
Function CleanPreviousInstall
  Push $0
  Push $1

  IfFileExists "$INSTDIR\launcher.version" 0 finish

  FindFirst $0 $1 "$INSTDIR\*.*"
loop:
  StrCmp $1 "" close
  StrCmp $1 "." next
  StrCmp $1 ".." next
  StrCmp $1 "config.json" next
  StrCmp $1 "launcher.version" next
  StrCmp $1 "launcher.update-status" next
  StrCmp $1 "Uninstall.exe" next

  IfFileExists "$INSTDIR\$1\*.*" 0 removefile
    RMDir /r "$INSTDIR\$1"
    Goto next
removefile:
    Delete "$INSTDIR\$1"
next:
  FindNext $0 $1
  Goto loop
close:
  FindClose $0

finish:
  Pop $1
  Pop $0
FunctionEnd

; Каталог установки проверяется на запись до начала распаковки.
;
; RequestExecutionLevel user + страница выбора каталога — сочетание, в котором
; пользователь свободно вписывает C:\Program Files\ChillHub, а прав на запись
; туда у процесса нет. Проверки не было, и установка падала на первом же файле:
; NSIS показывал системную ошибку записи, уже создав каталог и пройдя половину
; мастера.
Function DirectoryLeave
  ClearErrors
  CreateDirectory "$INSTDIR"
  IfErrors notwritable

  ; Создать каталог мало: право на создание подкаталога и право на запись
  ; файлов в него — разные вещи. Проверяем тем же действием, которое предстоит
  ; делать установке.
  FileOpen $0 "$INSTDIR\chillhub-write-test.tmp" w
  ${If} $0 == ""
    Goto notwritable
  ${EndIf}
  FileWrite $0 "chillhub"
  FileClose $0
  Delete "$INSTDIR\chillhub-write-test.tmp"

  ; Место на выбранном диске — здесь же: сказать «не поместится» на странице
  ; выбора каталога полезнее, чем на середине распаковки.
  Call CheckDiskSpace
  Return

notwritable:
  MessageBox MB_ICONSTOP \
    "В каталог «$INSTDIR» нельзя записывать.$\r$\n$\r$\n${APP_TITLE} устанавливается только для текущего пользователя и без прав администратора, поэтому системные каталоги (Program Files, Windows) не подойдут.$\r$\nВыберите каталог в своём профиле — например, $LOCALAPPDATA\${APP_NAME}." \
    /SD IDOK
  Abort
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

  ${NSD_CreateLabel} 0 0 100% 24 "Выберите папку для хранения игр, загруженных в лаунчере Chill Hub"
  Pop $GamesDir_Label

  ${NSD_CreateText} 0 28 80% 26 "$GAMES_DIR"
  Pop $GamesDir_Edit

  ${NSD_CreateBrowseButton} 82% 26 18% 26 "Обзор..."
  Pop $GamesDir_Browse
  ${NSD_OnClick} $GamesDir_Browse SelectGamesDir_Browse

  ; Ярлык на рабочем столе раньше создавался молча. Галочка стоит здесь, а не
  ; отдельной страницей: лишний шаг мастера ради одного чекбокса — плохая
  ; плата за возможность его не ставить.
  ${NSD_CreateCheckbox} 0 70 100% 18 "Создать ярлык на рабочем столе"
  Pop $DesktopShortcut_Check
  ${If} $DesktopShortcut_State == 1
    ${NSD_Check} $DesktopShortcut_Check
  ${EndIf}

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
  ; Состояние галочки снимается до того, как контролы страницы будут
  ; уничтожены: в секции установки читать их уже нечем.
  ${NSD_GetState} $DesktopShortcut_Check $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $DesktopShortcut_State 1
  ${Else}
    StrCpy $DesktopShortcut_State 0
  ${EndIf}

  ${NSD_GetText} $GamesDir_Edit $GAMES_DIR
  ${If} $GAMES_DIR == ""
    MessageBox MB_ICONEXCLAMATION "Укажите папку для установки игр." /SD IDOK
    Abort
  ${EndIf}
  ; Create if missing
  IfFileExists "$GAMES_DIR" +2 0
    CreateDirectory "$GAMES_DIR"
FunctionEnd

; =========================
; Finish page actions
; =========================
; Галочка «Доустановить WebView2» показывается только тем, кому он нужен.
;
; WebView2 предустановлен в Windows 11 и приезжает с Edge на Windows 10, то
; есть у подавляющего большинства он уже есть. Предлагать таким людям
; доустановку — предлагать сделать бессмысленную работу, а снятая галочка ещё и
; выглядит как отказ от новостей в лаунчере.
;
; $WebView2Present заполняется в .onInit. Контрол не просто прячется, но и
; снимается: скрытая, но отмеченная галочка всё равно вызвала бы InstallPrereqs
; по кнопке «Готово».
Function FinishPageShow
  ${If} $WebView2Present == 1
    SendMessage $mui.FinishPage.ShowReadme ${BM_SETCHECK} ${BST_UNCHECKED} 0
    ShowWindow $mui.FinishPage.ShowReadme ${SW_HIDE}
  ${EndIf}
FunctionEnd

Function RunAppAfterInstall
  ; If user ticked "Запустить Chill Hub", set a flag; actual launch may be deferred
  StrCpy $LaunchAfterFlag 1
FunctionEnd

; Проверяет, установлен ли WebView2 Runtime. Результат — в $WebView2Present
; (1 — есть, 0 — нет).
;
; Смотрим оба улья: рантайм ставится либо на машину (HKLM, обычный случай —
; приехал с Edge), либо в профиль пользователя (HKCU). Непустая версия в
; значении "pv" и означает установленный рантайм.
Function DetectWebView2
  StrCpy $WebView2Present 0
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_CLIENT_KEY}" "pv"
  StrCmp $0 "" 0 found
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_CLIENT_KEY}" "pv"
  StrCmp $0 "" 0 found
  ReadRegStr $0 HKCU "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_CLIENT_KEY}" "pv"
  StrCmp $0 "" done found
found:
  ; "0.0.0.0" Edge пишет как признак «зарегистрировано, но не установлено».
  StrCmp $0 "0.0.0.0" done 0
  StrCpy $WebView2Present 1
done:
FunctionEnd

Function InstallPrereqs
  ; Единственная оставшаяся зависимость — WebView2 Runtime. Инсталлятор .NET
  ; больше не нужен: сборка self-contained несёт рантайм внутри себя.
  StrCpy $PrereqsRan 1
  Call DetectWebView2
  StrCmp $WebView2Present 1 skip 0
  StrCpy $1 "$PLUGINSDIR\Redist\${PREREQ_WEBVIEW2}"
  IfFileExists "$1" 0 skip
    ; Bootstrapper сам решает, что качать; //silent /install проходит без окон.
    ExecWait '"$1" /silent /install'
skip:
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

  ; ГАЛОЧКИ УДАЛЕНИЯ ДОСТУПНЫ И В ТИХОМ РЕЖИМЕ.
  ;
  ; Обе они — единственное, что отличает «удалить лаунчер» от «убрать за собой
  ; всё»: папку с играми (десятки гигабайт) и настройки в %APPDATA%. Задать их
  ; скриптом было нельзя, а значит и ПРОВЕРИТЬ автоматически было нечем: тихое
  ; удаление всегда шло по ветке «ничего лишнего не трогаем», и ветка «галочка
  ; стоит» жила без единого прогона — при том, что она делает RMDir /r по пути
  ; из свободного текстового поля.
  ;
  ;   Uninstall.exe /S /DELETEGAMES /DELETESETTINGS
  ;
  ; Умолчание не меняется: без ключей тихое удаление, как и раньше, не трогает
  ; ни игры, ни настройки. Решение об удалении пользовательских данных
  ; принимает человек, а не отсутствие ответа.
  StrCpy $DeleteGames_State 0
  StrCpy $DeleteSettings_State 0
  ${un.GetParameters} $R0
  ClearErrors
  ${un.GetOptions} $R0 "/DELETEGAMES" $R1
  ${IfNot} ${Errors}
    StrCpy $DeleteGames_State 1
  ${EndIf}

  ClearErrors
  ${un.GetOptions} $R0 "/DELETESETTINGS" $R1
  ${IfNot} ${Errors}
    StrCpy $DeleteSettings_State 1
  ${EndIf}

  ClearErrors
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

  ; Настройки лаунчера (%APPDATA%\ChillHub) удаление не трогает намеренно: они
  ; переживают переустановку, и это ожидаемое поведение. Но выбора «снести всё»
  ; не было вовсе — оставался каталог, о котором пользователь уже не помнит.
  ; Галочка снята по умолчанию: молчание значит «сохранить».
  ${NSD_CreateCheckbox} 0 68 100% 18 "Удалить настройки лаунчера (тема, папки, лимиты)"
  Pop $DeleteSettings_Check

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

  ${NSD_GetState} $DeleteSettings_Check $2
  ${If} $2 == ${BST_CHECKED}
    StrCpy $DeleteSettings_State 1
  ${Else}
    StrCpy $DeleteSettings_State 0
  ${EndIf}
FunctionEnd
