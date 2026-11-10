//! An on-demand official YouTube iframe. No browser, COM apartment or loader
//! is initialized until `open`; all COM callbacks run on the owning UI thread.
use std::{
    cell::RefCell,
    path::{Path, PathBuf},
    rc::{Rc, Weak},
    time::{Duration, Instant},
};
use url::Url;
use webview2_com::{
    AcceleratorKeyPressedEventHandler, CoTaskMemPWSTR, CoreWebView2EnvironmentOptions,
    CreateCoreWebView2ControllerCompletedHandler, CreateCoreWebView2EnvironmentCompletedHandler,
    DownloadStartingEventHandler, Microsoft::Web::WebView2::Win32::*,
    NavigationCompletedEventHandler, NavigationStartingEventHandler,
    NewWindowRequestedEventHandler, PermissionRequestedEventHandler, ProcessFailedEventHandler,
    WebMessageReceivedEventHandler, WebResourceRequestedEventHandler,
};
use windows::{
    Win32::{
        Foundation::{HWND, RECT},
        System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize},
        UI::Input::KeyboardAndMouse::SetFocus,
    },
    core::{BOOL, HRESULT, Interface, PCWSTR, PWSTR},
};

const ORIGIN: &str = "https://launcher.samoy.love";
const PAGE: &str = "https://launcher.samoy.love/__chillhub_video/index.html";

#[derive(Clone, Copy, Debug, Default)]
pub struct WebVideoRect {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}
impl WebVideoRect {
    fn native(self) -> RECT {
        RECT {
            left: self.x,
            top: self.y,
            right: self
                .x
                .saturating_add(self.width.min(i32::MAX as u32) as i32),
            bottom: self
                .y
                .saturating_add(self.height.min(i32::MAX as u32) as i32),
        }
    }
}
#[derive(Clone, Debug, Default)]
pub struct WebVideoStatus {
    pub ready: bool,
    pub error: Option<String>,
    pub close_requested: bool,
    pub referer_configured: bool,
}

pub fn youtube_id(source: &str) -> Result<String, String> {
    let url = Url::parse(source).map_err(|_| "Некорректная ссылка YouTube")?;
    if url.scheme() != "https"
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
    {
        return Err("Нужна обычная HTTPS-ссылка YouTube".into());
    }
    let host = url.host_str().unwrap_or("");
    let parts: Vec<_> = url.path_segments().into_iter().flatten().collect();
    let id = match host {
        "youtu.be" if parts.len() == 1 => Some(parts[0].to_owned()),
        "youtube.com" | "www.youtube.com" | "m.youtube.com" | "www.youtube-nocookie.com" => {
            if url.path() == "/watch" {
                let values: Vec<_> = url
                    .query_pairs()
                    .filter(|(k, _)| k == "v")
                    .map(|(_, v)| v.into_owned())
                    .collect();
                if values.len() == 1 {
                    values.into_iter().next()
                } else {
                    None
                }
            } else if parts.len() == 2 && matches!(parts[0], "embed" | "shorts" | "live") {
                Some(parts[1].to_owned())
            } else {
                None
            }
        }
        _ => None,
    }
    .ok_or("В этой ссылке нет видео YouTube")?;
    if id.len() != 11
        || !id
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'_' || c == b'-')
    {
        return Err("Некорректный идентификатор видео".into());
    }
    Ok(id)
}
pub fn canonical_url(source: &str) -> Result<String, String> {
    Ok(format!(
        "https://www.youtube.com/watch?v={}",
        youtube_id(source)?
    ))
}

fn player_error(code: i64) -> String {
    match code {
        101 | 150 => {
            "Автор разрешает смотреть это видео только на YouTube. Откройте его на сайте.".into()
        }
        100 => "Видео удалено, скрыто или недоступно. Проверьте его на YouTube.".into(),
        153 => "YouTube не подтвердил подключение плеера. Откройте видео на сайте.".into(),
        _ => "Не удалось воспроизвести видео здесь. Можно открыть его на YouTube.".into(),
    }
}
fn process_failure_needs_reload(kind: COREWEBVIEW2_PROCESS_FAILED_KIND) -> bool {
    // WebView2 restarts GPU/utility/helper processes itself. Treating every
    // process event as fatal leaves a recovered player behind a permanent error.
    matches!(
        kind,
        COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED
            | COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED
            | COREWEBVIEW2_PROCESS_FAILED_KIND_FRAME_RENDER_PROCESS_EXITED
            | COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_UNRESPONSIVE
    )
}
fn wrapper(id: &str, nonce: &str) -> String {
    // Both inserted values are generated/validated ASCII, never arbitrary HTML.
    format!(
        r#"<!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="strict-origin-when-cross-origin"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-{nonce}' https://www.youtube.com https://s.ytimg.com; style-src 'nonce-{nonce}'; frame-src https://www.youtube.com https://www.youtube-nocookie.com; base-uri 'none'; form-action 'none'"><style nonce="{nonce}">html,body,#player{{margin:0;width:100%;height:100%;overflow:hidden;background:#000}}</style></head><body><div id="player"></div><script nonce="{nonce}">function send(type,code){{window.chrome.webview.postMessage(JSON.stringify({{type:type,code:code||0}}));}}function onYouTubeIframeAPIReady(){{window.player=new YT.Player('player',{{host:'https://www.youtube-nocookie.com',videoId:'{id}',width:'100%',height:'100%',playerVars:{{autoplay:1,playsinline:1,origin:'{ORIGIN}',rel:0,fs:0}},events:{{onReady:function(){{send('ready');}},onError:function(e){{send('error',e.data);}}}}}});}}window.addEventListener('error',function(){{send('load-error');}},true);</script><script nonce="{nonce}" src="https://www.youtube.com/iframe_api"></script></body></html>"#
    )
}

type CreateEnvironment = unsafe extern "system" fn(
    PCWSTR,
    PCWSTR,
    *mut std::ffi::c_void,
    *mut std::ffi::c_void,
) -> HRESULT;
struct Runtime {
    module: windows_sys::Win32::Foundation::HMODULE,
    create: CreateEnvironment,
}
impl Runtime {
    fn load() -> Result<Rc<Self>, String> {
        let path = std::env::current_exe()
            .map_err(|e| e.to_string())?
            .parent()
            .ok_or("Не найдена папка лаунчера")?
            .join("web-video/WebView2Loader.dll");
        crate::manifest::check_no_links(&path).map_err(|e| e.to_string())?;
        let wide: Vec<u16> = path
            .to_string_lossy()
            .encode_utf16()
            .chain(Some(0))
            .collect();
        unsafe {
            use windows_sys::Win32::System::LibraryLoader::{
                GetProcAddress, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32,
                LoadLibraryExW,
            };
            let module = LoadLibraryExW(
                wide.as_ptr(),
                std::ptr::null_mut(),
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
            );
            if module.is_null() {
                return Err("Не найден компонент просмотра видео. Откройте YouTube или переустановите лаунчер.".into());
            }
            let Some(address) = GetProcAddress(
                module,
                c"CreateCoreWebView2EnvironmentWithOptions".as_ptr().cast(),
            ) else {
                windows_sys::Win32::Foundation::FreeLibrary(module);
                return Err("Компонент просмотра видео повреждён".into());
            };
            let create: CreateEnvironment = std::mem::transmute(address);
            if let Err(error) = CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok() {
                windows_sys::Win32::Foundation::FreeLibrary(module);
                return Err(error.to_string());
            }
            Ok(Rc::new(Self { module, create }))
        }
    }
}
impl Drop for Runtime {
    fn drop(&mut self) {
        unsafe {
            CoUninitialize();
            windows_sys::Win32::Foundation::FreeLibrary(self.module);
        }
    }
}

struct State {
    alive: bool,
    parent: isize,
    rect: WebVideoRect,
    visible: bool,
    status: WebVideoStatus,
    started: Instant,
    controller: Option<ICoreWebView2Controller>,
    webview: Option<ICoreWebView2>,
    environment: Option<ICoreWebView2Environment>,
    content: PathBuf,
    video_id: String,
    _runtime: Rc<Runtime>,
    _profile_guard: crate::manifest::DirectoryGuard,
}
pub struct WebVideo {
    state: Rc<RefCell<State>>,
}
impl WebVideo {
    /// Call on the same UI thread as the parent HWND. No blocking message pump.
    pub fn open(
        parent_hwnd: isize,
        rect: WebVideoRect,
        source: &str,
        data_dir: &Path,
    ) -> Result<Self, String> {
        let id = youtube_id(source)?;
        let mut process = 0;
        unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId(
                parent_hwnd as _,
                &mut process,
            );
        }
        if process != std::process::id() {
            return Err("Плеер требует окно этого лаунчера".into());
        }
        let runtime = Runtime::load()?;
        let cache = data_dir.join("cache/web-video");
        let guard =
            crate::manifest::DirectoryGuard::acquire(&cache, true).map_err(|e| e.to_string())?;
        let content = cache.join("content");
        let page_dir = content.join("__chillhub_video");
        let _content_guard =
            crate::manifest::DirectoryGuard::acquire(&page_dir, true).map_err(|e| e.to_string())?;
        crate::paths::atomic_write(
            &page_dir.join("index.html"),
            wrapper(&id, &uuid::Uuid::new_v4().simple().to_string()).as_bytes(),
        )
        .map_err(|e| e.to_string())?;
        let profile = cache.join("profile");
        crate::manifest::check_no_links(&profile).map_err(|e| e.to_string())?;
        let state = Rc::new(RefCell::new(State {
            alive: true,
            parent: parent_hwnd,
            rect,
            visible: true,
            status: WebVideoStatus::default(),
            started: Instant::now(),
            controller: None,
            webview: None,
            environment: None,
            content,
            video_id: id,
            _runtime: runtime.clone(),
            _profile_guard: guard,
        }));
        let weak = Rc::downgrade(&state);
        let pending_runtime = runtime.clone();
        let handler = CreateCoreWebView2EnvironmentCompletedHandler::create(Box::new(
            move |result, environment| {
                let _keep_runtime = &pending_runtime;
                let Some(state) = weak.upgrade().filter(|s| s.borrow().alive) else {
                    return Ok(());
                };
                match result.and_then(|_| {
                    environment.ok_or_else(|| {
                        windows::core::Error::from_hresult(windows::Win32::Foundation::E_POINTER)
                    })
                }) {
                    Ok(environment) => {
                        state.borrow_mut().environment = Some(environment.clone());
                        let weak = Rc::downgrade(&state);
                        let runtime = pending_runtime.clone();
                        let handler = CreateCoreWebView2ControllerCompletedHandler::create(
                            Box::new(move |result, controller| {
                                let _keep_runtime = &runtime;
                                let Some(state) = weak.upgrade().filter(|s| s.borrow().alive)
                                else {
                                    if let Some(c) = controller {
                                        unsafe {
                                            let _ = c.Close();
                                        }
                                    }
                                    return Ok(());
                                };
                                match result.and_then(|_| {
                                    controller.ok_or_else(|| {
                                        windows::core::Error::from_hresult(
                                            windows::Win32::Foundation::E_POINTER,
                                        )
                                    })
                                }) {
                                    Ok(controller) => {
                                        if let Err(error) = configure(&state, controller.clone()) {
                                            unsafe {
                                                let _ = controller.Close();
                                            }
                                            fail(&Rc::downgrade(&state), &error.to_string());
                                        }
                                    }
                                    Err(error) => fail(&Rc::downgrade(&state), &error.to_string()),
                                }
                                Ok(())
                            }),
                        );
                        if let Err(error) = unsafe {
                            environment
                                .CreateCoreWebView2Controller(HWND(parent_hwnd as _), &handler)
                        } {
                            fail(&Rc::downgrade(&state), &error.to_string());
                        }
                    }
                    Err(error) => fail(
                        &weak,
                        &format!(
                            "Не удалось открыть плеер WebView2: {error}. Можно смотреть на YouTube."
                        ),
                    ),
                }
                Ok(())
            },
        ));
        let options: ICoreWebView2EnvironmentOptions =
            CoreWebView2EnvironmentOptions::default().into();
        unsafe {
            options.SetAdditionalBrowserArguments(windows::core::w!("--autoplay-policy=no-user-gesture-required --disk-cache-size=33554432 --media-cache-size=16777216" )).map_err(|e|e.to_string())?;
            options
                .SetTargetCompatibleBrowserVersion(windows::core::w!("109.0.1518.0"))
                .map_err(|e| e.to_string())?;
            let wide: Vec<u16> = profile
                .to_string_lossy()
                .encode_utf16()
                .chain(Some(0))
                .collect();
            (runtime.create)(
                PCWSTR::null(),
                PCWSTR(wide.as_ptr()),
                options.as_raw(),
                handler.as_raw(),
            )
            .ok()
            .map_err(|e| {
                format!("Не удалось открыть плеер WebView2: {e}. Можно смотреть на YouTube.")
            })?;
        }
        Ok(Self { state })
    }
    pub fn status(&self) -> WebVideoStatus {
        let mut state = self.state.borrow_mut();
        if state.alive
            && !state.status.ready
            && state.status.error.is_none()
            && state.started.elapsed() >= Duration::from_secs(25)
        {
            state.status.error = Some(
                "YouTube не ответил вовремя. Проверьте подключение или откройте видео на сайте."
                    .into(),
            );
        }
        state.status.clone()
    }
    pub fn set_rect(&mut self, rect: WebVideoRect) -> Result<(), String> {
        let controller = {
            let mut s = self.state.borrow_mut();
            s.rect = rect;
            s.controller.clone()
        };
        if let Some(c) = controller {
            unsafe {
                c.SetBounds(rect.native()).map_err(|e| e.to_string())?;
                c.NotifyParentWindowPositionChanged()
                    .map_err(|e| e.to_string())?;
            }
        }
        Ok(())
    }
    pub fn set_visible(&mut self, visible: bool) -> Result<(), String> {
        let controller = {
            let mut s = self.state.borrow_mut();
            s.visible = visible;
            s.controller.clone()
        };
        if let Some(c) = controller {
            unsafe {
                c.SetIsVisible(visible).map_err(|e| e.to_string())?;
            }
        }
        Ok(())
    }
    pub fn close(&mut self) {
        let (controller, webview, environment) = {
            let mut s = self.state.borrow_mut();
            s.alive = false;
            s.status.ready = false;
            (s.controller.take(), s.webview.take(), s.environment.take())
        };
        unsafe {
            if let Some(w) = &webview {
                let _ = w.Stop();
            }
            if let Some(c) = &controller {
                let _ = c.Close();
            }
        }
        drop((controller, webview, environment));
    }
}
impl Drop for WebVideo {
    fn drop(&mut self) {
        self.close();
    }
}
fn fail(state: &Weak<RefCell<State>>, error: &str) {
    if let Some(s) = state.upgrade()
        && let Ok(mut s) = s.try_borrow_mut()
        && s.alive
    {
        s.status.error = Some(error.to_owned());
        s.status.ready = false;
    }
}
fn read_string(
    call: impl FnOnce(*mut PWSTR) -> windows::core::Result<()>,
) -> windows::core::Result<String> {
    let mut value = PWSTR::null();
    call(&mut value)?;
    Ok(CoTaskMemPWSTR::from(value).to_string())
}

fn configure(
    state: &Rc<RefCell<State>>,
    controller: ICoreWebView2Controller,
) -> windows::core::Result<()> {
    let (rect, visible, content) = {
        let s = state.borrow();
        (s.rect, s.visible, s.content.clone())
    };
    unsafe {
        controller.SetIsVisible(false)?;
        if let Ok(c) = controller.cast::<ICoreWebView2Controller3>() {
            c.SetBoundsMode(COREWEBVIEW2_BOUNDS_MODE_USE_RAW_PIXELS)?;
        }
        controller.SetBounds(rect.native())?;
        let webview = controller.CoreWebView2()?;
        let settings = webview.Settings()?;
        settings.SetAreDevToolsEnabled(false)?;
        settings.SetAreDefaultContextMenusEnabled(false)?;
        settings.SetAreDefaultScriptDialogsEnabled(false)?;
        settings.SetIsStatusBarEnabled(false)?;
        settings.SetIsZoomControlEnabled(false)?;
        settings.SetAreHostObjectsAllowed(false)?;
        settings.SetIsWebMessageEnabled(true)?;
        if let Ok(s) = settings.cast::<ICoreWebView2Settings3>() {
            s.SetAreBrowserAcceleratorKeysEnabled(false)?;
        }
        if let Ok(s) = settings.cast::<ICoreWebView2Settings4>() {
            s.SetIsPasswordAutosaveEnabled(false)?;
            s.SetIsGeneralAutofillEnabled(false)?;
        }
        let folder: Vec<u16> = content
            .to_string_lossy()
            .encode_utf16()
            .chain(Some(0))
            .collect();
        webview
            .cast::<ICoreWebView2_3>()?
            .SetVirtualHostNameToFolderMapping(
                windows::core::w!("launcher.samoy.love"),
                PCWSTR(folder.as_ptr()),
                COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS,
            )?;
        let mut token = 0;
        webview.add_NavigationStarting(
            &NavigationStartingEventHandler::create(Box::new(|_, args| {
                if let Some(args) = args {
                    let uri = read_string(|p| args.Uri(p))?;
                    args.SetCancel(uri != PAGE)?;
                }
                Ok(())
            })),
            &mut token,
        )?;
        let weak = Rc::downgrade(state);
        webview.add_NavigationCompleted(
            &NavigationCompletedEventHandler::create(Box::new(move |_, args| {
                if let Some(args) = args {
                    let mut success = BOOL(0);
                    args.IsSuccess(&mut success)?;
                    let mut error = COREWEBVIEW2_WEB_ERROR_STATUS_UNKNOWN;
                    args.WebErrorStatus(&mut error)?;
                    // A deliberately blocked outside link does not break the
                    // still-playing iframe on the current page.
                    if !success.as_bool()
                        && error != COREWEBVIEW2_WEB_ERROR_STATUS_OPERATION_CANCELED
                    {
                        fail(
                            &weak,
                            "Не удалось загрузить страницу видео. Можно открыть её на YouTube.",
                        );
                    }
                }
                Ok(())
            })),
            &mut token,
        )?;
        let weak = Rc::downgrade(state);
        webview.add_ProcessFailed(
            &ProcessFailedEventHandler::create(Box::new(move |_, args| {
                if let Some(args) = args {
                    let mut kind = COREWEBVIEW2_PROCESS_FAILED_KIND_UNKNOWN_PROCESS_EXITED;
                    args.ProcessFailedKind(&mut kind)?;
                    if process_failure_needs_reload(kind) {
                        fail(&weak, "Плеер остановился. Закройте видео и откройте его снова либо смотрите на YouTube.");
                    }
                }
                Ok(())
            })), &mut token,
        )?;
        webview.add_NewWindowRequested(
            &NewWindowRequestedEventHandler::create(Box::new(|_, args| {
                if let Some(args) = args {
                    args.SetHandled(true)?;
                }
                Ok(())
            })),
            &mut token,
        )?;
        webview.add_PermissionRequested(
            &PermissionRequestedEventHandler::create(Box::new(|_, args| {
                if let Some(args) = args {
                    args.SetState(COREWEBVIEW2_PERMISSION_STATE_DENY)?;
                }
                Ok(())
            })),
            &mut token,
        )?;
        webview.cast::<ICoreWebView2_4>()?.add_DownloadStarting(
            &DownloadStartingEventHandler::create(Box::new(|_, args| {
                if let Some(args) = args {
                    args.SetCancel(true)?;
                    args.SetHandled(true)?;
                }
                Ok(())
            })),
            &mut token,
        )?;
        let weak = Rc::downgrade(state);
        webview.add_WebMessageReceived(
            &WebMessageReceivedEventHandler::create(Box::new(move |_, args| {
                if let Some(args) = args {
                    if read_string(|p| args.Source(p))? != PAGE {
                        return Ok(());
                    }
                    let message = read_string(|p| args.TryGetWebMessageAsString(p))?;
                    if message.len() > 256 {
                        return Ok(());
                    }
                    if let Ok(value) = serde_json::from_str::<serde_json::Value>(&message)
                        && let Some(state) = weak.upgrade()
                        && let Ok(mut state) = state.try_borrow_mut()
                        && state.alive
                    {
                        apply_player_message(&mut state.status, &value);
                    }
                }
                Ok(())
            })),
            &mut token,
        )?;
        let weak = Rc::downgrade(state);
        controller.add_AcceleratorKeyPressed(
            &AcceleratorKeyPressedEventHandler::create(Box::new(move |_, args| {
                if let Some(args) = args {
                    let mut key = 0;
                    args.VirtualKey(&mut key)?;
                    let mut kind = COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN;
                    args.KeyEventKind(&mut kind)?;
                    if key == 27
                        && kind == COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN
                        && let Some(state) = weak.upgrade()
                    {
                        let (view, parent) = {
                            let s = state.borrow();
                            (s.webview.clone(), s.parent)
                        };
                        let mut fullscreen = BOOL(0);
                        if let Some(v) = view {
                            v.ContainsFullScreenElement(&mut fullscreen)?;
                        }
                        if !fullscreen.as_bool() {
                            args.SetHandled(true)?;
                            if let Ok(mut s) = state.try_borrow_mut() {
                                s.status.close_requested = true;
                            }
                            let _ = SetFocus(Some(HWND(parent as _)));
                        }
                    }
                }
                Ok(())
            })),
            &mut token,
        )?;
        for filter in [
            "https://www.youtube.com/embed/*",
            "https://www.youtube-nocookie.com/embed/*",
        ] {
            let wide: Vec<u16> = filter.encode_utf16().chain(Some(0)).collect();
            webview.AddWebResourceRequestedFilter(
                PCWSTR(wide.as_ptr()),
                COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT,
            )?;
        }
        let weak = Rc::downgrade(state);
        webview.add_WebResourceRequested(
            &WebResourceRequestedEventHandler::create(Box::new(move |_, args| {
                if let Some(args) = args
                    && let Some(state) = weak.upgrade()
                {
                    let request = args.Request()?;
                    let uri = read_string(|p| request.Uri(p))?;
                    let allowed = Url::parse(&uri).ok().is_some_and(|u| {
                        u.scheme() == "https"
                            && matches!(
                                u.host_str(),
                                Some("www.youtube.com" | "www.youtube-nocookie.com")
                            )
                            && u.path() == format!("/embed/{}", state.borrow().video_id)
                    });
                    if allowed {
                        request.Headers()?.SetHeader(
                            windows::core::w!("Referer"),
                            windows::core::w!("https://launcher.samoy.love/"),
                        )?;
                        if let Ok(mut s) = state.try_borrow_mut() {
                            s.status.referer_configured = true;
                        }
                    }
                }
                Ok(())
            })),
            &mut token,
        )?;
        {
            let mut s = state.borrow_mut();
            s.controller = Some(controller.clone());
            s.webview = Some(webview.clone());
        }
        webview.Navigate(windows::core::w!(
            "https://launcher.samoy.love/__chillhub_video/index.html"
        ))?;
        controller.SetIsVisible(visible)?;
    }
    Ok(())
}

fn apply_player_message(status: &mut WebVideoStatus, value: &serde_json::Value) {
    match value.get("type").and_then(|v| v.as_str()) {
        Some("ready") => {
            status.ready = true;
            status.error = None;
        }
        Some("error") => {
            status.ready = false;
            status.error = Some(player_error(
                value.get("code").and_then(|v| v.as_i64()).unwrap_or(0),
            ));
        }
        Some("load-error") if !status.ready => {
            status.error = Some(
                "Не удалось загрузить YouTube. Проверьте подключение или откройте видео на сайте."
                    .into(),
            );
        }
        _ => {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_real_youtube_video_links_are_accepted() {
        for link in [
            "https://youtu.be/aB_12-cD345",
            "https://www.youtube.com/watch?v=aB_12-cD345&t=9",
            "https://youtube.com/shorts/aB_12-cD345",
        ] {
            assert_eq!(youtube_id(link).unwrap(), "aB_12-cD345");
        }
        for link in [
            "http://youtu.be/aB_12-cD345",
            "https://www.youtube.com.evil.test/watch?v=aB_12-cD345",
            "https://user@youtube.com/watch?v=aB_12-cD345",
            "https://youtube.com/watch?v=short",
            "https://youtube.com/watch?v=aB_12-cD345&v=aB_12-cD345",
            "file:///youtube.com/watch?v=aB_12-cD345",
        ] {
            assert!(youtube_id(link).is_err(), "{link}");
        }
    }
    #[test]
    fn player_uses_official_api_and_first_party_identity_without_fallback_bypass() {
        let html = wrapper("aB_12-cD345", "testnonce");
        assert!(html.contains("strict-origin-when-cross-origin"));
        assert!(html.contains("https://www.youtube.com/iframe_api"));
        assert!(html.contains(ORIGIN));
        assert!(html.contains("youtube-nocookie.com"));
        assert!(!html.contains("video/mp4"));
        assert!(player_error(101).contains("Автор"));
        assert_eq!(player_error(101), player_error(150));
    }
    #[test]
    fn playback_messages_recover_loading_but_keep_owner_restrictions_visible() {
        let mut status = WebVideoStatus::default();
        apply_player_message(&mut status, &serde_json::json!({"type":"load-error"}));
        assert!(status.error.is_some());
        apply_player_message(&mut status, &serde_json::json!({"type":"ready"}));
        assert!(status.ready && status.error.is_none());
        apply_player_message(&mut status, &serde_json::json!({"type":"load-error"}));
        assert!(status.ready && status.error.is_none());
        apply_player_message(&mut status, &serde_json::json!({"type":"error","code":150}));
        assert!(!status.ready && status.error.unwrap().contains("Автор"));
    }
    #[test]
    fn recoverable_browser_subprocess_exits_do_not_hide_the_player() {
        for kind in [
            COREWEBVIEW2_PROCESS_FAILED_KIND_GPU_PROCESS_EXITED,
            COREWEBVIEW2_PROCESS_FAILED_KIND_UTILITY_PROCESS_EXITED,
            COREWEBVIEW2_PROCESS_FAILED_KIND_SANDBOX_HELPER_PROCESS_EXITED,
            COREWEBVIEW2_PROCESS_FAILED_KIND_UNKNOWN_PROCESS_EXITED,
        ] {
            assert!(!process_failure_needs_reload(kind));
        }
        assert!(process_failure_needs_reload(
            COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED
        ));
        assert!(process_failure_needs_reload(
            COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED
        ));
        assert!(process_failure_needs_reload(
            COREWEBVIEW2_PROCESS_FAILED_KIND_FRAME_RENDER_PROCESS_EXITED
        ));
    }
}
