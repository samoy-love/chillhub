use super::{
    Activity, AppConfig, AppWindow, Path, PathBuf, State, background, background_for, close_panel,
    open_panel, refresh_disk, reload, save_window, settings_ui, toast,
};
use chillhub_native::feedback::FeedbackService;
use std::sync::atomic::{AtomicU64, Ordering};

static LIVE_SETTINGS_EPOCH: AtomicU64 = AtomicU64::new(0);
const SAVING_LIVE: &str = "Сохраняем…";

pub(super) enum AfterSave {
    Close,
    Quit,
    OpenPanel(String),
}

impl AfterSave {
    pub(super) fn from_panel(value: &str) -> Self {
        match value {
            "" => Self::Close,
            "__quit" => Self::Quit,
            panel => Self::OpenPanel(panel.to_owned()),
        }
    }
}

pub(super) fn load(ui: &AppWindow, state: &State) {
    ui.set_settings_error("".into());
    let s = state.borrow();
    let c = &s.prefs;
    ui.set_settings_path(
        s.services
            .paths
            .games_dir
            .to_string_lossy()
            .into_owned()
            .into(),
    );
    ui.set_settings_threads(c.download_threads);
    ui.set_settings_speed(c.speed_limit_mbps);
    ui.set_settings_api(c.api_base_url.clone().into());
    ui.set_settings_tray(c.minimize_to_tray);
    ui.set_karaoke_enabled(
        c.extra
            .get("KaraokeEnabled")
            .and_then(|v| v.as_bool())
            .unwrap_or(false),
    );
    ui.set_reduced_motion(
        c.extra
            .get("ReducedMotion")
            .and_then(|v| v.as_bool())
            .unwrap_or(false),
    );
    drop(s);
    refresh_disk(ui, state);
}
pub(super) fn save_live(ui: &AppWindow, state: &State) {
    let threads = ui.get_settings_threads();
    let speed = ui.get_settings_speed();
    let tray = ui.get_settings_tray();
    let reduced = ui.get_reduced_motion();
    let karaoke = ui.get_karaoke_enabled();
    let s = state.borrow();
    if ui.get_settings_save_status() != SAVING_LIVE
        && s.prefs.download_threads == threads
        && s.prefs
            .extra
            .get("KaraokeEnabled")
            .and_then(|v| v.as_bool())
            .unwrap_or(false)
            == karaoke
        && s.prefs.speed_limit_mbps == speed
        && s.prefs.minimize_to_tray == tray
        && s.prefs
            .extra
            .get("ReducedMotion")
            .and_then(|v| v.as_bool())
            .unwrap_or(false)
            == reduced
    {
        return;
    }
    let services = s.services.clone();
    drop(s);
    let epoch = LIVE_SETTINGS_EPOCH.fetch_add(1, Ordering::SeqCst) + 1;
    ui.set_settings_save_status(SAVING_LIVE.into());
    background(
        ui,
        move || {
            services.config.update(|c| {
                if LIVE_SETTINGS_EPOCH.load(Ordering::SeqCst) == epoch {
                    c.download_threads = threads;
                    c.speed_limit_mbps = speed;
                    c.minimize_to_tray = tray;
                    c.extra.insert("ReducedMotion".into(), reduced.into());
                    c.extra.insert("KaraokeEnabled".into(), karaoke.into());
                }
            })
        },
        move |ui, state, result| {
            if LIVE_SETTINGS_EPOCH.load(Ordering::SeqCst) != epoch {
                return;
            }
            match result {
                Ok(saved) => {
                    let mut s = state.borrow_mut();
                    s.prefs.download_threads = saved.download_threads;
                    s.prefs.speed_limit_mbps = saved.speed_limit_mbps;
                    s.prefs.minimize_to_tray = saved.minimize_to_tray;
                    s.prefs.extra.insert("ReducedMotion".into(), reduced.into());
                    s.prefs
                        .extra
                        .insert("KaraokeEnabled".into(), karaoke.into());
                    s.services
                        .queue
                        .set_speed_limit(saved.speed_limit_mbps.max(0) as u64 * 1024 * 1024);
                    ui.set_settings_save_status("Сохранено".into());
                }
                Err(e) => {
                    ui.set_settings_save_status("Не удалось сохранить".into());
                    ui.set_settings_error(e.into());
                }
            }
        },
    );
}
pub(super) fn save(ui: &AppWindow, state: &State, next: AfterSave) {
    if ui.get_operation_busy() {
        return;
    }
    let services = state.borrow().services.clone();
    let path = settings_ui::normalize_games_path(ui.get_settings_path().as_str())
        .to_string_lossy()
        .into_owned();
    let api = ui
        .get_settings_api()
        .trim()
        .trim_end_matches('/')
        .to_string();
    let threads = ui.get_settings_threads();
    let speed = ui.get_settings_speed();
    let tray = ui.get_settings_tray();
    let reduced = ui.get_reduced_motion();
    let current = state.borrow().prefs.clone();
    let karaoke = ui.get_karaoke_enabled();
    let karaoke_changed = current
        .extra
        .get("KaraokeEnabled")
        .and_then(|v| v.as_bool())
        .unwrap_or(false)
        != karaoke;
    let paths_changed = !settings_ui::same_path(&path, &services.paths.games_dir.to_string_lossy());
    let api_changed = api != current.api_base_url.trim_end_matches('/');
    let reload_needed = paths_changed || api_changed;
    let reduced_changed = current
        .extra
        .get("ReducedMotion")
        .and_then(|v| v.as_bool())
        .unwrap_or(false)
        != reduced;
    if ui.get_settings_save_status() != SAVING_LIVE
        && !reload_needed
        && current.download_threads == threads
        && current.speed_limit_mbps == speed
        && current.minimize_to_tray == tray
        && !reduced_changed
        && !karaoke_changed
    {
        finish_settings(ui, state, &next);
        return;
    }
    ui.set_settings_error("".into());
    if let Err(error) = settings_ui::validate_path_override(
        &path,
        std::env::var_os("CHILLHUB_GAMES_DIR").as_deref(),
    ) {
        ui.set_settings_error(error.into());
        return;
    }
    if reload_needed && !state.borrow().launching.is_empty() {
        ui.set_settings_error("Дождитесь запуска игр перед изменением папки или сервера.".into());
        return;
    }
    LIVE_SETTINGS_EPOCH.fetch_add(1, Ordering::SeqCst);
    ui.set_settings_save_status("".into());
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        move || -> Result<AppConfig, String> {
            if reload_needed && !services.idle() {
                return Err("Папку игр и адрес сервера можно изменить после завершения загрузок и игр. Скорость, потоки и поведение окна можно менять сейчас.".into());
            }
            if paths_changed {
                let folder = PathBuf::from(&path);
                let executable = std::env::current_exe().map_err(|e| e.to_string())?;
                chillhub_native::library::validate_games_location(
                    &folder,
                    &services.paths,
                    executable.parent().ok_or("Папка лаунчера не найдена")?,
                )?;
            }
            if api_changed {
                chillhub_native::net::ApiClient::new(&api).map_err(|e| e.to_string())?;
            }
            let saved = services.config.update(|c| {
                if paths_changed {
                    c.games_path = path;
                }
                if api_changed {
                    c.api_base_url = api;
                }
                c.download_threads = threads;
                c.speed_limit_mbps = speed;
                c.minimize_to_tray = tray;
                c.extra
                    .insert("ReducedMotion".into(), serde_json::Value::Bool(reduced));
                c.extra.insert("KaraokeEnabled".into(), karaoke.into());
            })?;
            services
                .queue
                .set_speed_limit(saved.speed_limit_mbps.max(0) as u64 * 1024 * 1024);
            if paths_changed {
                chillhub_native::installed_apps::refresh(
                    Path::new(&saved.games_path),
                    &services.paths.data_dir,
                );
            }
            Ok(saved)
        },
        move |ui, state, result| {
            ui.set_operation_busy(false);
            match result {
                Ok(config) => {
                    let mut s = state.borrow_mut();
                    s.services.paths = config.apply_paths(&s.services.paths);
                    if api_changed {
                        match chillhub_native::net::ApiClient::new(&config.api_base_url) {
                            Ok(api) => {
                                let data = s.services.paths.data_dir.clone();
                                s.services.replace_api(api.with_diagnostics(data));
                            }
                            Err(e) => {
                                drop(s);
                                ui.set_settings_error(e.to_string().into());
                                return;
                            }
                        }
                        s.services.feedback = std::sync::Arc::new(FeedbackService::new(
                            s.services.api.clone(),
                            s.services.paths.data_dir.clone(),
                        ));
                    }
                    s.prefs = config;
                    drop(s);
                    load(ui, state);
                    finish_settings(ui, state, &next);
                    toast(
                        ui,
                        state,
                        if paths_changed {
                            "Настройки сохранены. Игры остались в прежней папке."
                        } else {
                            "Настройки сохранены"
                        },
                    );
                    if reload_needed && !matches!(next, AfterSave::Quit) {
                        reload(ui, state);
                    }
                }
                Err(e) => {
                    ui.set_settings_error(e.clone().into());
                    toast(ui, state, &e);
                }
            }
        },
    );
}
fn finish_settings(ui: &AppWindow, state: &State, next: &AfterSave) {
    close_panel(ui, state);
    match next {
        AfterSave::Close => {}
        AfterSave::Quit => {
            save_window(ui, state);
            let _ = slint::quit_event_loop();
        }
        AfterSave::OpenPanel(panel) => open_panel(ui, state, panel),
    }
}
