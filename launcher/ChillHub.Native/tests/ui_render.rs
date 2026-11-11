//! Real Slint components rendered by the headless software backend. No native
//! window, network request, launcher controller, helper, or game is started.
//! CHILLHUB_UI_QA=1 also enables compiler element debug information and writes
//! the expanded size/DPI matrix to dist/qa-renders for visual inspection.
use chillhub_native::ui::{
    AppWindow, AuxiliaryWindow, GameRow, MediaTile, ModBenefit, ModRow, OrphanRow, QueueRow,
    SetupWindow,
};
use i_slint_backend_testing::{
    ElementHandle, ElementRoot, TestingBackend, TestingBackendOptions, mock_elapsed_time,
};
use serde_json::Value;
use slint::{
    ComponentHandle, Image, LogicalSize, ModelRc, SharedString, VecModel,
    platform::{Key, PointerEventButton, WindowEvent},
};
use std::{
    cell::RefCell,
    path::{Path, PathBuf},
    rc::Rc,
    time::Duration,
};

fn model<T: Clone + 'static>(rows: Vec<T>) -> ModelRc<T> {
    ModelRc::new(VecModel::from(rows))
}
fn strings(value: &Value) -> ModelRc<SharedString> {
    model(
        value
            .as_array()
            .unwrap()
            .iter()
            .map(|s| s.as_str().unwrap().into())
            .collect(),
    )
}
fn text<'a>(value: &'a Value, name: &str) -> &'a str {
    value[name].as_str().unwrap_or("")
}
fn picture(base: &Path, relative: &str) -> Image {
    Image::load_from_path(&base.join(relative))
        .unwrap_or_else(|error| panic!("{relative}: {error}"))
}
fn settle() {
    mock_elapsed_time(Duration::from_millis(300));
}
fn key(window: &slint::Window, key: Key) {
    window.dispatch_event(WindowEvent::KeyPressed { text: key.into() });
    window.dispatch_event(WindowEvent::KeyReleased { text: key.into() });
    settle();
}
fn resize(window: &slint::Window, width: u32, height: u32, scale: f32) {
    window.dispatch_event(WindowEvent::ScaleFactorChanged {
        scale_factor: scale,
    });
    // TestingWindow::set_size in 1.18.1 converts both dimensions at scale 1.
    // Keep its physical snapshot allocation and the logical layout consistent.
    window.set_size(slint::PhysicalSize::new(
        (width as f32 * scale).round() as u32,
        (height as f32 * scale).round() as u32,
    ));
    window.dispatch_event(WindowEvent::Resized {
        size: LogicalSize::new(width as f32, height as f32),
    });
}

/// CPU raster diagnostic, deliberately separate from the correctness gate and
/// live RAM/GPU runs. Snapshots force full frames and exclude DWM presentation.
#[cfg(feature = "benchmark")]
#[test]
fn software_continuous_resize_diagnostic() {
    use slint::Model;
    slint::platform::set_platform(Box::new(TestingBackend::new(TestingBackendOptions {
        mock_time: true,
        threading: false,
        renderer_name: Some("software".into()),
    })))
    .unwrap();
    let mut h = Harness::new();
    h.game("lethal-company");
    let premultiplied =
        |image: Image| Image::from_rgba8_premultiplied(image.to_rgba8_premultiplied().unwrap());
    h.ui.set_hero(premultiplied(h.ui.get_hero()));
    h.ui.set_media(model(
        h.ui.get_media()
            .iter()
            .map(|mut tile| {
                tile.poster = premultiplied(tile.poster);
                tile
            })
            .collect(),
    ));
    resize(h.ui.window(), 1707, 996, 1.5);
    settle();
    h.ui.window().take_snapshot().unwrap();
    i_slint_renderer_software::frame_timings::enable(true);
    for step in 0..120 {
        let width = 1707 - (step % 60) * 10;
        resize(h.ui.window(), width, 996, 1.5);
        mock_elapsed_time(Duration::from_millis(16));
        let snapshot = h.ui.window().take_snapshot().unwrap();
        assert_eq!(snapshot.width(), (width as f32 * 1.5).round() as u32);
    }
    let samples = i_slint_renderer_software::frame_timings::samples();
    i_slint_renderer_software::frame_timings::enable(false);
    assert_eq!(samples.len(), 120);
    let output = h.base.join("dist/software-resize-timings.json");
    std::fs::create_dir_all(output.parent().unwrap()).unwrap();
    std::fs::write(output, serde_json::to_vec_pretty(&serde_json::json!({
        "samples": samples,
        "scope": "120 full CPU raster frames at 150% DPI, changing width, warm assets; excludes snapshot conversion and DWM presentation",
    })).unwrap()).unwrap();
}

struct Harness {
    ui: AppWindow,
    base: PathBuf,
    catalog: Vec<Value>,
    actions: Rc<RefCell<Vec<String>>>,
    qa: bool,
    renders: Vec<Value>,
}
impl Harness {
    fn new() -> Self {
        let base = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
        let catalog: Vec<Value> = serde_json::from_str(include_str!("../Data/games.json")).unwrap();
        let ui = AppWindow::new().unwrap();
        let actions = Rc::new(RefCell::new(Vec::new()));
        let log = actions.clone();
        ui.on_action_requested(move |a| log.borrow_mut().push(a.into()));
        let log = actions.clone();
        ui.on_feedback_send(move |_, _, kind, _, _| {
            log.borrow_mut().push(format!("feedback:{kind}"))
        });
        let weak = ui.as_weak();
        ui.on_game_selected(move |index| weak.upgrade().unwrap().set_selected_game(index));
        let log = actions.clone();
        ui.on_queue_action(move |id, a| log.borrow_mut().push(format!("queue:{id}:{a}")));
        let log = actions.clone();
        ui.on_orphan_action(move |id, a| log.borrow_mut().push(format!("orphan:{id}:{a}")));
        let log = actions.clone();
        ui.on_settings_save(move |_, threads, speed, _, _, _| {
            log.borrow_mut().push(format!("settings:{threads}:{speed}"))
        });
        let log = actions.clone();
        let weak = ui.as_weak();
        ui.on_settings_leave(move |_| {
            let ui = weak.upgrade().unwrap();
            log.borrow_mut().push(format!(
                "settings:{}:{}",
                ui.get_settings_threads(),
                ui.get_settings_speed()
            ));
        });
        let log = actions.clone();
        ui.on_version_selected(move |version| log.borrow_mut().push(format!("version:{version}")));
        let log = actions.clone();
        ui.on_open_media_source(move || log.borrow_mut().push("open-media-source".to_owned()));
        let log = actions.clone();
        ui.on_video_toggle(move || log.borrow_mut().push("video-toggle".to_owned()));
        let weak = ui.as_weak();
        ui.on_panel_closed(move || weak.upgrade().unwrap().set_panel_kind("".into()));
        let weak = ui.as_weak();
        ui.on_panel_requested(move |p| weak.upgrade().unwrap().set_panel_kind(p));
        let weak = ui.as_weak();
        ui.on_source_selected(move |s| weak.upgrade().unwrap().set_source(s));
        let weak = ui.as_weak();
        ui.on_mode_selected(move |m| weak.upgrade().unwrap().set_mode(m));
        let weak = ui.as_weak();
        ui.on_mod_category_selected(move |category| {
            weak.upgrade().unwrap().set_mod_category(category);
        });
        ui.set_games(model(
            catalog
                .iter()
                .map(|g| GameRow {
                    id: text(g, "Id").into(),
                    title: text(g, "Title").into(),
                    icon: picture(&base, text(g, "Icon")),
                    has_mods: g["HasMods"].as_bool().unwrap(),
                    status: "Установлена".into(),
                    status_kind: "ready".into(),
                })
                .collect(),
        ));
        ui.set_settings_path(r"D:\Games\ChillHub".into());
        ui.set_settings_api("https://launcher.samoy.love".into());
        ui.set_settings_threads(4);
        ui.set_settings_speed(10);
        ui.set_settings_tray(true);
        ui.set_disk_label("Свободно 236 ГБ".into());
        ui.set_downloads_active(2);
        ui.set_downloads_summary("Скачиваем · 49%".into());
        ui.set_downloads_progress(0.49);
        ui.set_manage_path(
            r"D:\Игры\Chill Hub\Длинный путь к установленной копии игры\PEAK".into(),
        );
        ui.set_manage_details("На диске: 8,6 ГБ\nПоследняя версия в каталоге: 2.6.A\nПоследняя игра: 10.11.2026 19:42 · 53 мин".into());
        ui.set_has_modpack_link(true);
        ui.set_can_play(true);
        ui.set_launcher_version(env!("CARGO_PKG_VERSION").into());
        ui.set_releases(model(
            chillhub_native::changelog::entries_since("1.7.20")
                .into_iter()
                .map(|r| chillhub_native::ui::ReleaseRow {
                    version: r.version.into(),
                    date: r.date.into(),
                    changes: model(r.items.into_iter().map(Into::into).collect()),
                    fresh: r.fresh,
                })
                .collect(),
        ));
        ui.set_can_verify_game(true);
        ui.set_can_verify_mods(true);
        ui.set_has_game_files(true);
        ui.set_can_create_shortcut(true);
        ui.set_installed_version("Установлена 2.6.B".into());
        ui.set_playtime_label("12 ч 42 мин".into());
        ui.set_versions(model(vec!["2.6.B".into(), "2.6.A".into(), "2.5".into()]));
        ui.set_selected_version("2.6.B".into());
        ui.set_mod_categories(model(
            [
                "Все",
                "Геймплей",
                "Контент",
                "Удобство",
                "Интерфейс",
                "Косметика",
                "Звук",
                "Исправления",
                "Зависимости",
                "Сборка",
            ]
            .iter()
            .map(|s| (*s).into())
            .collect(),
        ));
        let mut h = Self {
            ui,
            base,
            catalog,
            actions,
            qa: std::env::var_os("CHILLHUB_UI_QA").is_some(),
            renders: Vec::new(),
        };
        h.game("peak");
        h.ui.show().unwrap();
        h
    }
    fn game(&mut self, id: &str) {
        let index = self
            .catalog
            .iter()
            .position(|g| text(g, "Id") == id)
            .unwrap();
        let g = &self.catalog[index];
        self.ui.set_panel_kind("".into());
        self.ui.set_game_title(text(g, "Title").into());
        self.ui.set_selected_game(index as i32);
        self.ui.set_description(text(g, "Description").into());
        self.ui.set_genre(text(g, "Genre").into());
        self.ui
            .set_hero(picture(&self.base, &format!("art/{id}/hero.webp")));
        self.ui.set_hero_progress(1.0);
        self.ui
            .set_supports_steam(g["SteamSupported"].as_bool().unwrap());
        self.ui
            .set_supports_local(g["LocalSupported"].as_bool().unwrap());
        self.ui.set_has_mods(g["HasMods"].as_bool().unwrap());
        self.ui.set_pack_name(text(g, "ModpackName").into());
        self.ui.set_pack_version(text(g, "ModpackVersion").into());
        self.ui
            .set_modpack_description(text(g, "ModpackDescription").into());
        self.ui.set_highlights(strings(&g["Highlights"]));
        self.ui
            .set_mod_count(g["Mods"].as_array().unwrap().len() as i32);
        let benefits = g.get("ModBenefits").or_else(|| g.get("modBenefits"));
        self.ui.set_mod_benefits(model(
            benefits
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .map(|b| ModBenefit {
                    title: b
                        .get("Title")
                        .or_else(|| b.get("title"))
                        .and_then(Value::as_str)
                        .unwrap_or("")
                        .into(),
                    description: b
                        .get("Description")
                        .or_else(|| b.get("description"))
                        .and_then(Value::as_str)
                        .unwrap_or("")
                        .into(),
                    icon: b
                        .get("Icon")
                        .or_else(|| b.get("icon"))
                        .and_then(Value::as_str)
                        .unwrap_or("package")
                        .into(),
                })
                .collect(),
        ));
        self.ui
            .set_installed_version(format!("Установлена {}", text(g, "GameVersion")).into());
        self.ui.set_status("Готова к запуску".into());
        let media = g["Media"].as_array().unwrap();
        self.ui.set_has_gallery(
            media
                .iter()
                .any(|m| text(m, "Kind") == "video" || text(m, "Path") != text(g, "Hero")),
        );
        self.ui.set_media(model(
            media
                .iter()
                .enumerate()
                .map(|(i, m)| MediaTile {
                    // Remote media loading belongs to the controller/network
                    // tests. Layout tests keep its unloaded poster state local.
                    poster: if self.base.join(format!("art/{id}/tile-{i}.webp")).is_file() {
                        picture(&self.base, &format!("art/{id}/tile-{i}.webp"))
                    } else if self.base.join(text(m, "Path")).is_file() {
                        picture(&self.base, text(m, "Path"))
                    } else {
                        Image::default()
                    },
                    caption: text(m, "Caption").into(),
                    is_video: text(m, "Kind") == "video",
                    is_external: text(m, "Provider") == "youtube"
                        || text(m, "Playback") == "external"
                        || text(m, "Url").contains("youtube.com")
                        || text(m, "Url").contains("youtu.be"),
                })
                .collect(),
        ));
        self.ui
            .set_gallery_image(picture(&self.base, text(&media[0], "Path")));
        self.ui
            .set_gallery_caption(text(&media[0], "Caption").into());
        self.ui.set_gallery_index(0);
        self.ui.set_mods(model(
            g["Mods"]
                .as_array()
                .unwrap()
                .iter()
                .map(|m| ModRow {
                    name: format!("{} · {}", text(m, "RussianName"), text(m, "Name")).into(),
                    description: text(m, "Description").into(),
                    category: text(m, "Category").into(),
                    version: text(m, "Version").into(),
                })
                .collect(),
        ));
    }
    fn panel(&self, kind: &str, title: &str, body: &str) {
        self.ui.set_panel_kind("".into());
        settle();
        self.ui.set_panel_title(title.into());
        self.ui.set_panel_subtitle("Chill Hub".into());
        self.ui.set_panel_body(body.into());
        self.ui.set_panel_kind(kind.into());
        settle();
    }
    fn click(&self, label: &str) {
        let element = ElementHandle::find_by_accessible_label(&self.ui, label)
            .find(|e| e.accessible_enabled().unwrap_or(true))
            .unwrap_or_else(|| panic!("Missing enabled control: {label}"));
        element.invoke_accessible_default_action();
        settle();
    }
    fn capture(&mut self, name: &str, width: u32, height: u32, scale: f32) {
        resize(self.ui.window(), width, height, scale);
        settle();
        let snapshot = self
            .ui
            .window()
            .take_snapshot()
            .expect("headless software snapshot");
        assert_eq!(snapshot.width(), (width as f32 * scale).round() as u32);
        assert_eq!(snapshot.height(), (height as f32 * scale).round() as u32);
        assert!(
            snapshot
                .as_bytes()
                .as_chunks::<4>()
                .0
                .iter()
                .any(|p| p[0] > 100 && p[1] > 100 && p[2] > 100),
            "{name}: missing content"
        );
        if self.qa {
            let elements = self.ui.root_element().query_descendants().find_all();
            assert!(elements.len() > 20, "missing compiler debug info");
            for e in elements {
                let p = e.absolute_position();
                let s = e.size();
                assert!(
                    [p.x, p.y, s.width, s.height].iter().all(|v| v.is_finite()),
                    "nonfinite geometry: {:?}",
                    e.id()
                );
                assert!(
                    s.width >= -0.1 && s.height >= -0.1,
                    "negative size {:?}: {s:?}",
                    e.id()
                );
            }
            let id = if self.ui.get_panel_kind().is_empty() {
                "AppWindow::sidebar"
            } else {
                "AppWindow::modal"
            };
            let container = ElementHandle::find_by_element_id(&self.ui, id)
                .next()
                .expect(id);
            assert_in_bounds(&container, width as f32, height as f32, name);
            if !self.ui.get_panel_kind().is_empty() {
                let close = ElementHandle::find_by_element_id(&self.ui, "AppWindow::modal-close")
                    .next()
                    .unwrap();
                assert_in_bounds(&close, width as f32, height as f32, name);
                let form_id = match self.ui.get_panel_kind().as_str() {
                    "settings" => Some("AppWindow::settings-form"),
                    "feedback" => Some("AppWindow::feedback-form"),
                    _ => None,
                };
                if let Some(id) = form_id {
                    let form = ElementHandle::find_by_element_id(&self.ui, id)
                        .next()
                        .unwrap();
                    assert!(
                        form.absolute_position().x + form.size().width
                            <= container.absolute_position().x + container.size().width - 44.0
                                + 0.5,
                        "form reserves a separate scrollbar gutter: {id}"
                    );
                }
            }
            let output = self.base.join("dist/qa-renders");
            std::fs::create_dir_all(&output).unwrap();
            let filename = format!("{name}-{width}x{height}-{}pct.png", (scale * 100.0) as u32);
            image::save_buffer(
                output.join(&filename),
                snapshot.as_bytes(),
                snapshot.width(),
                snapshot.height(),
                image::ColorType::Rgba8,
            )
            .unwrap();
            self.renders.push(serde_json::json!({"file":filename,"logicalWidth":width,"logicalHeight":height,"scale":scale,"physicalWidth":snapshot.width(),"physicalHeight":snapshot.height()}));
        }
    }
    fn assert_title_and_gutter(&self) {
        let title = ElementHandle::find_by_element_id(&self.ui, "AppWindow::hero-title")
            .next()
            .unwrap();
        let description =
            ElementHandle::find_by_element_id(&self.ui, "AppWindow::hero-description")
                .next()
                .unwrap();
        assert!(
            (title.absolute_position().y - 163.0).abs() < 0.5,
            "the title anchor must not move with description length"
        );
        assert!(
            title.size().height >= 40.0,
            "title uses its full line metrics"
        );
        assert!(
            description.absolute_position().y
                >= title.absolute_position().y + title.size().height + 5.0,
            "title and description must not overlap"
        );
        let list = ElementHandle::find_by_element_id(&self.ui, "AppWindow::library-scroll")
            .next()
            .unwrap();
        let selected =
            ElementHandle::find_by_accessible_label(&self.ui, self.ui.get_game_title().as_str())
                .find(|e| e.accessible_enabled().is_some())
                .expect("selected game remains visible after resize");
        assert!(
            selected.absolute_position().y >= list.absolute_position().y - 0.5
                && selected.absolute_position().y + selected.size().height
                    <= list.absolute_position().y + list.size().height + 0.5,
            "selected library row is fully visible"
        );
        let mut visible_rows = 0;
        for game in &self.catalog {
            let Some(row) = ElementHandle::find_by_accessible_label(&self.ui, text(game, "Title"))
                .find(|e| e.accessible_enabled().is_some())
            else {
                continue;
            };
            visible_rows += 1;
            assert!(
                row.absolute_position().x + row.size().width
                    <= list.absolute_position().x + list.size().width - 17.0,
                "library row covers scrollbar gutter: {}",
                text(game, "Title")
            );
        }
        assert!(visible_rows > 0, "library must contain visible rows");
    }
}
fn assert_in_bounds(element: &ElementHandle, width: f32, height: f32, context: &str) {
    let p = element.absolute_position();
    let s = element.size();
    assert!(
        p.x >= -1.0
            && p.y >= -1.0
            && p.x + s.width <= width + 1.0
            && p.y + s.height <= height + 1.0,
        "{context}: {:?} position={p:?} size={s:?} window={width}x{height}",
        element.id()
    );
}

#[test]
fn real_ui_headless_layout_and_actions() {
    slint::platform::set_platform(Box::new(TestingBackend::new(TestingBackendOptions {
        mock_time: true,
        threading: false,
        renderer_name: Some("software".into()),
    })))
    .expect("headless platform only");
    let mut h = Harness::new();
    h.capture("main", 1360, 850, 1.0);
    let karaoke_ticks = Rc::new(std::cell::Cell::new(0));
    let counter = karaoke_ticks.clone();
    h.ui.on_karaoke_tick(move || counter.set(counter.get() + 1));
    h.ui.set_karaoke_enabled(true);
    h.ui.set_karaoke_text("Фирменная строка лаунчера".into());
    h.ui.set_karaoke_next("Следующая строка — аккуратно в одну линию".into());
    settle();
    assert_eq!(
        karaoke_ticks.get(),
        0,
        "inactive window must not wake the timer"
    );
    h.ui.set_karaoke_active(true);
    mock_elapsed_time(Duration::ZERO); // Apply declarative timer bindings first.
    settle();
    assert!(
        karaoke_ticks.get() > 0,
        "visible active header must advance"
    );
    h.capture("karaoke", 1360, 850, 1.0);
    h.ui.set_reduced_motion(true);
    h.ui.set_karaoke_fading(true);
    mock_elapsed_time(Duration::ZERO);
    let paused = karaoke_ticks.get();
    settle();
    assert_eq!(
        karaoke_ticks.get(),
        paused,
        "reduced motion must stop the timer"
    );
    h.capture("karaoke-reduced", 1360, 850, 1.0);
    h.ui.set_karaoke_fading(false);
    h.ui.set_reduced_motion(false);
    h.capture("karaoke-small", 900, 500, 1.0);
    let paused = karaoke_ticks.get();
    settle();
    assert_eq!(
        karaoke_ticks.get(),
        paused,
        "hidden narrow header must not wake the timer"
    );
    h.ui.set_karaoke_enabled(false);
    h.ui.set_karaoke_active(false);
    if h.qa {
        h.ui.set_operation_busy(true);
        settle();
        for label in ["Играть", "Steam", "Пиратка", "С модами", "Без модов"]
        {
            let control = ElementHandle::find_by_accessible_label(&h.ui, label)
                .find(|e| e.accessible_enabled().is_some())
                .expect(label);
            assert_eq!(
                control.accessible_enabled(),
                Some(false),
                "busy guard: {label}"
            );
        }
        h.ui.set_operation_busy(false);
        h.capture("main-small", 900, 500, 1.0);
        let previous_source = h.ui.get_source();
        h.ui.set_source("steam".into());
        h.ui.set_status("Игра не установлена в Steam".into());
        h.ui.set_can_play(false);
        h.capture("unavailable-steam-small", 900, 500, 1.0);
        h.ui.set_source(previous_source);
        h.ui.set_status("Готова к запуску".into());
        h.ui.set_can_play(true);
        h.ui.set_maintenance_note("Меняем диск на сервере раздачи и проверяем файлы. Ожидаемое окончание — 10.11 в 22:30. Установленные игры остаются доступны.".into());
        h.capture("maintenance", 900, 500, 1.0);
        h.ui.set_maintenance_note("".into());
        h.ui.set_install_hint("Недостаточно места. Подготовить файлы: 43,2 ГБ · нужно места: 46,7 ГБ · свободно 19,1 ГБ".into());
        h.capture("install-estimate", 900, 500, 1.0);
        h.ui.set_install_hint("".into());
        for label in [
            "Восстановить моды",
            "Установить моды",
            "Технические работы",
            "Игра запущена",
        ] {
            h.ui.set_play_label(label.into());
            h.capture(
                &format!("action-{}", label.replace(' ', "-")),
                900,
                500,
                1.0,
            );
        }
        h.ui.set_play_label("Играть".into());
        for w in [1120, 1360, 1600] {
            for he in [720, 850] {
                for scale in [1.0, 1.5, 2.0] {
                    h.capture("main", w, he, scale);
                }
            }
        }
        let ids: Vec<String> = h.catalog.iter().map(|g| text(g, "Id").to_owned()).collect();
        for id in ids {
            h.game(&id);
            for (w, he, scale) in [
                (900, 500, 1.0),
                (1360, 850, 1.0),
                (1360, 850, 1.5),
                (900, 650, 2.0),
            ] {
                h.capture(&format!("title-{id}"), w, he, scale);
                h.assert_title_and_gutter();
            }
            let title = ElementHandle::find_by_element_id(&h.ui, "AppWindow::hero-title")
                .next()
                .unwrap();
            title.scroll(0.0, -1000.0);
            h.capture(&format!("benefits-{id}"), 1360, 850, 1.0);
            ElementHandle::find_by_element_id(&h.ui, "AppWindow::main-scroll")
                .next()
                .unwrap()
                .scroll(0.0, 1000.0);
        }
        h.game("peak");
        h.ui.set_description("Длинное описание для проверки переноса: друзья исследуют большой мир, обсуждают находки и вместе выбирают следующий маршрут. Текст должен полностью оставаться доступным, а название игры — сохранять положение и целую нижнюю часть букв. Небольшое окно не должно скрывать ни управление запуском, ни описание, ни действия модпака.".into());
        h.ui.set_mod_benefits(model((0..5).map(|i| ModBenefit {
            title: format!("Группа преимуществ {} с длинным названием", i + 1).into(),
            description: "Подробное описание группы проверяет перенос текста и естественную высоту карточки без обрезки строк.".into(),
            icon: ["users", "gamepad", "sparkles", "wrench", "compass"][i].into(),
        }).collect()));
        h.capture("long-copy", 900, 500, 1.0);
        h.assert_title_and_gutter();
        ElementHandle::find_by_element_id(&h.ui, "AppWindow::hero-title")
            .next()
            .unwrap()
            .scroll(0.0, -1000.0);
        h.capture("long-benefits", 900, 500, 1.0);
        h.game("peak");
        // Game changes normally reset this scroll position in the controller.
        ElementHandle::find_by_element_id(&h.ui, "AppWindow::main-scroll")
            .next()
            .unwrap()
            .scroll(0.0, 1000.0);
        for source in ["steam", "local"] {
            for mode in ["mods", "plain"] {
                h.ui.set_source(source.into());
                h.ui.set_mode(mode.into());
                h.capture(&format!("choice-{source}-{mode}"), 1120, 720, 1.5);
            }
        }
        h.game("drive-beyond-horizons");
        h.ui.set_has_gallery(false);
        h.capture("long-title-cover", 900, 650, 1.0);
        h.game("peak");
        h.ui.set_connection_note("Нет связи · установленные игры доступны".into());
        h.capture("offline", 1120, 720, 2.0);
        h.ui.set_connection_note("".into());
        let library = h.ui.get_games();
        h.ui.set_games(model(vec![]));
        h.ui.set_game_title("".into());
        h.ui.set_loading(true);
        h.capture("loading", 1120, 720, 1.0);
        h.ui.set_loading(false);
        h.ui.set_load_error("Сервер недоступен. Проверьте подключение и повторите попытку. Установленные игры останутся на компьютере.".into());
        h.capture("catalog-error", 1120, 720, 1.0);
        h.ui.set_load_error("".into());
        h.capture("empty", 1120, 720, 1.0);
        h.panel("security", "Антивирус", "");
        h.capture("security-empty-library", 900, 500, 1.0);
        let helper = ElementHandle::find_by_accessible_label(&h.ui, "Настроить антивирус")
            .find(|e| e.accessible_enabled().is_some())
            .unwrap();
        assert_eq!(helper.accessible_enabled(), Some(false));
        let before = h.actions.borrow().len();
        helper.invoke_accessible_default_action();
        settle();
        assert_eq!(
            h.actions.borrow().len(),
            before,
            "empty catalog cannot open a no-op helper"
        );
        h.panel("", "", "");
        h.ui.set_games(library);
        h.game("peak");
    }
    let cases = [
        ("settings", "Настройки", ""),
        (
            "downloads",
            "Загрузки",
            "Загруженные файлы проверяются перед установкой.",
        ),
        ("mods", "Состав модпака · 233", ""),
        (
            "manage",
            "Управление игрой",
            "Проверка восстановит файлы выбранной копии. Личные сохранения останутся на месте.",
        ),
        (
            "help",
            "Помощь",
            "Chill Hub помогает устанавливать игры и готовые сборки модов.",
        ),
        (
            "security",
            "Антивирус",
            "Проверка читает состояние защиты и историю обнаружений. Запись в истории сама по себе не означает, что файл сейчас находится в карантине.",
        ),
        (
            "update",
            "Обновление Chill Hub",
            "Доступна версия 1.9.0. После загрузки лаунчер перезапустится.",
        ),
        (
            "feedback",
            "Идея или ошибка",
            "Не отправляйте пароли и другие секретные данные.",
        ),
        (
            "confirm",
            "Удалить PEAK?",
            r"Будет удалена только папка D:\Games\ChillHub\peak. Steam-копия останется на месте. Это действие нельзя отменить.",
        ),
        (
            "changelog",
            "Что нового",
            "1.8.0\nБиблиотека, установка, модпаки и обновления в новом интерфейсе.\n\n1.7.20\nИсправлена установка модов и восстановление загрузок.",
        ),
        (
            "orphans",
            "Игры вне каталога",
            "Эти игры остались на диске, но больше не опубликованы. Их можно оставить или удалить выбранные папки. Сохранения внутри удаляемых папок тоже будут удалены.",
        ),
    ];
    h.ui.set_queue(model(vec![QueueRow{id:"running".into(),title:"PEAK · PeakFriendsEdition 2.0.15".into(),detail:"BepInEx/plugins/AdditionalContent/библиотека-модификаций.dll\nФайлы: 104 из 213\nОбработано 4,2 ГБ из 8,6 ГБ\nИз интернета: 1,8 ГБ · 18 МБ/с\nИспользовано с диска: 2,4 ГБ".into(),progress:0.49,state:"Скачивается".into(),can_pause:true,can_cancel:true,..Default::default()},QueueRow{id:"failure".into(),title:"Lethal Company".into(),detail:"Не удалось получить файл. Соединение прервалось во время скачивания длинного пути BepInEx/plugins/AdditionalContent/VeryLongRussianFileName/конфигурация-модификации.json. Уже загруженные данные сохранены; можно продолжить.".into(),progress:0.28,state:"Ошибка — можно продолжить".into(),can_resume:true,can_cancel:true,..Default::default()},QueueRow{id:"confirm".into(),title:"R.E.P.O. · Moo Modpack".into(),detail:"Перед заменой файлов закройте игру и подтвердите продолжение.".into(),progress:0.75,state:"Нужно подтверждение".into(),can_confirm:true,can_cancel:true,..Default::default()}]));
    h.ui.set_orphans(model(vec![OrphanRow{id:"old-game".into(),title:"Игра из прежнего каталога с длинным названием".into(),path:r"D:\Игры для совместных вечеров\Большая коллекция\ChillHub\old-game\очень-длинное-имя-папки-без-пробелов-для-проверки-переноса".into(),selected:true,..Default::default()}]));
    h.ui.set_orphan_selection_count(1);
    let original_orphans = h.ui.get_orphans();
    h.ui.set_metadata_note(h.ui.get_modpack_description());
    h.ui.set_task_progress(0.62);
    h.ui.set_task_status("Проверено 62% · уже загруженные файлы сохранены".into());
    h.ui.set_confirm_text("Удалить игру".into());
    let panel_sizes = if h.qa {
        vec![
            (900, 500, 1.0),
            (1120, 720, 2.0),
            (1360, 850, 1.0),
            (1600, 850, 1.5),
        ]
    } else {
        vec![(1120, 720, 1.0)]
    };
    for (kind, title, body) in cases {
        h.panel(kind, title, body);
        h.ui.set_update_available(kind == "update");
        h.ui.set_update_target("1.9.0".into());
        for &(w, he, scale) in &panel_sizes {
            h.capture(kind, w, he, scale);
        }
        if kind == "settings" {
            for section in [1, 2] {
                h.ui.set_settings_section(section);
                for &(w, he, scale) in &panel_sizes {
                    h.capture(&format!("settings-section-{section}"), w, he, scale);
                }
            }
            h.ui.set_settings_section(0);
        }
    }
    if h.qa {
        for (name, checked, available, error, busy, body) in [
            (
                "update-checking",
                false,
                false,
                false,
                true,
                "Ищем новую версию Chill Hub…",
            ),
            (
                "update-current",
                true,
                false,
                false,
                false,
                "У вас актуальная версия Chill Hub.",
            ),
            (
                "update-unreachable",
                true,
                false,
                true,
                false,
                "Не удалось проверить обновления. Проверьте подключение и попробуйте снова.",
            ),
            (
                "update-install-error",
                true,
                true,
                true,
                false,
                "Не удалось установить обновление. Закройте остальные окна лаунчера и повторите попытку.",
            ),
        ] {
            h.panel("update", "Обновление Chill Hub", body);
            h.ui.set_update_checked(checked);
            h.ui.set_update_available(available);
            h.ui.set_update_error(error);
            h.ui.set_operation_busy(busy);
            h.ui.set_task_progress(0.);
            h.ui.set_task_status("".into());
            for (w, he, scale) in [(900, 500, 1.0), (1360, 850, 1.5)] {
                h.capture(name, w, he, scale);
            }
            let label = if busy {
                "Подождите…"
            } else if available {
                "Обновить и перезапустить"
            } else if error {
                "Попробовать снова"
            } else {
                "Проверить обновления"
            };
            assert_eq!(
                ElementHandle::find_by_accessible_label(&h.ui, "Проверить снова")
                    .any(|e| e.accessible_enabled().is_some()),
                available,
                "secondary check is shown only alongside an available update"
            );
            let button = ElementHandle::find_by_accessible_label(&h.ui, label)
                .find(|e| e.accessible_enabled().is_some())
                .unwrap();
            let before = h.actions.borrow().len();
            button.invoke_accessible_default_action();
            settle();
            if busy {
                assert_eq!(
                    h.actions.borrow().len(),
                    before,
                    "checking cannot start another operation"
                );
            } else {
                assert_eq!(
                    h.actions.borrow().last().map(String::as_str),
                    Some(if available {
                        "update-install"
                    } else {
                        "update-check"
                    })
                );
            }
        }
        h.ui.set_operation_busy(false);
        h.ui.set_update_error(false);
        h.panel("changelog", "Что нового", "");
        let releases = h.ui.get_releases();
        h.ui.set_releases(model(vec![]));
        h.capture("changelog-empty", 900, 500, 1.0);
        h.click("Понятно");
        assert!(h.ui.get_panel_kind().is_empty());
        h.ui.set_releases(releases);
        h.panel("mods", "Состав модпака", "");
        resize(h.ui.window(), 900, 500, 1.0);
        settle();
        let category = ElementHandle::find_by_accessible_label(&h.ui, "Категория модов")
            .find(|e| e.accessible_enabled() == Some(true))
            .expect("all mod categories remain available in a compact selector");
        assert_in_bounds(&category, 900.0, 500.0, "compact mod categories");
        category.invoke_accessible_default_action();
        settle();
        for _ in 0..9 {
            key(h.ui.window(), Key::DownArrow);
        }
        h.capture("mods-category-menu-small", 900, 500, 1.0);
        key(h.ui.window(), Key::Return);
        assert_eq!(h.ui.get_mod_category(), "Сборка");
        assert_eq!(h.ui.get_panel_kind(), "mods");
        h.capture("mods-category-small", 900, 500, 1.0);
        h.ui.set_mod_category("Все".into());
    }
    h.ui.set_can_verify_game(false);
    h.ui.set_can_verify_mods(false);
    h.ui.set_has_game_files(false);
    h.ui.set_can_create_shortcut(false);
    let installed_version = h.ui.get_installed_version();
    let manage_details = h.ui.get_manage_details();
    let playtime = h.ui.get_playtime_label();
    h.ui.set_installed_version("Не установлена".into());
    h.ui.set_manage_details("На диске: 0 Б\nПоследняя версия в каталоге: 2.6.B".into());
    h.ui.set_playtime_label("Ещё не играли".into());
    h.panel(
        "manage",
        "Управление игрой",
        "Игра ещё не установлена. Выберите источник и нажмите «Установить» на карточке игры.",
    );
    h.capture("manage-uninstalled", 900, 500, 1.0);
    if h.qa {
        for label in [
            "Проверить игру",
            "Проверить модпак",
            "Папка игры",
            "Создать ярлык",
            "Удалить локальные файлы",
        ] {
            let mut element = None;
            for _ in 0..16 {
                element = ElementHandle::find_by_accessible_label(&h.ui, label)
                    .find(|e| e.accessible_enabled().is_some());
                if element.is_some() {
                    break;
                }
                ElementHandle::find_by_element_id(&h.ui, "AppWindow::management-scroll")
                    .next()
                    .expect("manage scroll")
                    .scroll(0., -80.);
                settle();
            }
            let element = element
                .unwrap_or_else(|| panic!("Manage action cannot be reached by scrolling: {label}"));
            assert_eq!(
                element.accessible_enabled(),
                Some(false),
                "Uninstalled action enabled: {label}"
            );
        }
    }
    h.ui.set_can_verify_game(true);
    h.ui.set_can_verify_mods(true);
    h.ui.set_has_game_files(true);
    h.ui.set_can_create_shortcut(true);
    h.ui.set_installed_version(installed_version);
    h.ui.set_manage_details(manage_details);
    h.ui.set_playtime_label(playtime);
    if h.qa {
        h.panel("confirm","В эту папку нельзя записать файлы",r"Папка: D:\Игры\ChillHub. Выберите другую папку для новых установок. Уже установленные игры не будут перемещены.");
        h.ui.set_confirm_text("Выбрать другую папку".into());
        h.capture("install-path-denied", 900, 500, 1.0);
        h.panel("confirm","Настройка защиты","Если защита Windows мешает установке или запуску, помощник покажет доступные настройки. Его можно открыть позже в настройках лаунчера. Открытие помощника не меняет настройки антивируса.");
        h.ui.set_confirm_text("Открыть помощник".into());
        h.capture("security-offer", 900, 500, 1.0);
        let modal = ElementHandle::find_by_element_id(&h.ui, "AppWindow::modal")
            .next()
            .unwrap();
        assert!(
            modal.size().height < 350.0,
            "short confirmations fit their content"
        );
        h.ui.set_panel_body(
            "Длинное пояснение с подробностями операции. "
                .repeat(80)
                .into(),
        );
        h.capture("confirmation-long", 900, 500, 1.5);
        for label in ["Отмена", h.ui.get_confirm_text().as_str()] {
            let action = ElementHandle::find_by_accessible_label(&h.ui, label)
                .find(|e| e.accessible_enabled() == Some(true))
                .expect("confirmation action remains visible without scrolling");
            assert_in_bounds(&action, 900.0, 500.0, label);
            assert!(action.absolute_position().y > 350.0);
        }
        let content = ElementHandle::find_by_element_id(&h.ui, "AppWindow::confirmation")
            .next()
            .unwrap();
        assert!(
            modal.size().height <= 436.0,
            "long confirmations stay inside the window"
        );
        assert!(
            content.size().height > modal.size().height,
            "long content remains scrollable"
        );
        h.panel(
            "orphans",
            "Удаление выбранных игр",
            "Обработка завершена. Результат указан у каждой папки.",
        );
        h.ui.set_orphans(model(vec![
            OrphanRow {
                id: "one".into(),
                title: "Прежняя игра".into(),
                path: r"D:\Games\ChillHub\one".into(),
                result: "Удалено".into(),
                ..Default::default()
            },
            OrphanRow {
                id: "two".into(),
                title: "Вторая игра".into(),
                path: r"D:\Games\ChillHub\two".into(),
                result:
                    "Не удалено: папка занята запущенной игрой. Закройте игру и повторите попытку."
                        .into(),
                ..Default::default()
            },
        ]));
        h.ui.set_orphan_selection_count(0);
        h.capture("orphans-results", 900, 500, 1.0);
    }
    h.panel("downloads", "Загрузки", "");
    if h.qa {
        h.click("Приостановить");
        assert!(
            h.actions
                .borrow()
                .contains(&"queue:running:pause".to_string())
        );
        for (name, state, actions) in [
            (
                "waiting",
                "В очереди",
                [true, false, true, false, true, false, false],
            ),
            (
                "running",
                "Скачиваем",
                [true, false, true, false, false, false, false],
            ),
            ("pausing", "Приостанавливаем…", [false; 7]),
            (
                "paused",
                "На паузе",
                [false, true, true, false, true, false, false],
            ),
            ("cancelling", "Отменяем…", [false; 7]),
            (
                "completed",
                "Готово",
                [false, false, false, false, false, true, false],
            ),
            (
                "failed",
                "Не удалось завершить",
                [false, true, false, false, false, true, false],
            ),
            (
                "blocked",
                "Не удалось завершить",
                [false, true, false, false, false, true, true],
            ),
            (
                "cancelled",
                "Отменено",
                [false, true, false, false, false, true, false],
            ),
            (
                "confirmation",
                "Требуется подтверждение",
                [false, false, true, true, false, false, false],
            ),
        ] {
            h.ui.set_queue(model(vec![QueueRow {
                id: name.into(),
                title: "PEAK · модпак".into(),
                state: state.into(),
                detail: if name == "pausing" || name == "cancelling" {
                    "Ждём безопасного завершения текущей операции с файлом."
                } else if name == "completed" {
                    "Все файлы установлены и проверены."
                } else {
                    "Обработано 4,2 ГБ из 8,6 ГБ"
                }
                .into(),
                progress: if name == "completed" { 1.0 } else { 0.49 },
                can_pause: actions[0],
                can_resume: actions[1],
                can_cancel: actions[2],
                can_confirm: actions[3],
                can_prioritize: actions[4],
                can_remove: actions[5],
                security_blocked: actions[6],
            }]));
            for (w, height, scale) in [(900, 500, 1.0), (1120, 720, 2.0)] {
                h.capture(&format!("queue-{name}"), w, height, scale);
                for (index, label) in [
                    "Приостановить",
                    "Продолжить",
                    "Отменить",
                    "Подтвердить",
                    "В начало",
                    "Убрать из списка",
                    "Антивирус",
                ]
                .iter()
                .enumerate()
                {
                    let button = ElementHandle::find_by_accessible_label(&h.ui, label)
                        .find(|e| e.accessible_enabled().is_some());
                    assert_eq!(button.is_some(), actions[index], "{name}: {label}");
                    if let Some(button) = button {
                        assert_in_bounds(&button, w as f32, height as f32, label);
                    }
                }
            }
        }
    }
    h.ui.set_queue(model(Vec::new()));
    h.capture("queue-empty", 1120, 720, 1.0);
    if h.qa {
        h.ui.set_queue_error("Не удалось прочитать сохранённую очередь: файл слишком большой. Исходная очередь оставлена на месте. Новые загрузки пока недоступны. Уже установленные игры и сохранения не изменены. Если ошибка повторится после перезапуска, приложите журнал к сообщению в поддержку.".into());
        h.capture("queue-storage-error", 900, 500, 1.0);
        h.ui.set_queue_error("".into());
    }
    h.ui.set_orphans(original_orphans);
    h.ui.set_orphan_selection_count(1);
    h.panel(
        "orphans",
        "Игры вне каталога",
        "Выберите папки, которые хотите удалить. Остальные игры останутся на месте.",
    );
    if h.qa {
        h.click("Удалить игру");
        assert!(
            h.actions
                .borrow()
                .contains(&"orphan:old-game:delete".to_string())
        );
    }
    h.ui.set_orphans(model(Vec::new()));
    h.ui.set_orphan_selection_count(0);
    h.ui.set_panel_body("Все установленные игры есть в каталоге.".into());
    h.capture("orphans-empty", 1120, 720, 1.0);
    h.panel("gallery", "Галерея", "");
    h.ui.set_selected_media_is_video(false);
    h.capture("gallery-image", 1120, 720, 2.0);
    h.ui.set_selected_media_is_video(true);
    h.ui.set_video_duration(103.0);
    h.ui.set_video_position(37.0);
    h.ui.set_video_time_label("0:37 / 1:43".into());
    h.capture("gallery-video-controls", 1360, 850, 1.0);
    if h.qa {
        h.capture("gallery-video-small", 900, 500, 1.0);
    }
    // The child HWND video surface is intentionally not created in a UI test.
    assert!(h.ui.get_show_video());
    assert!(h.ui.get_video_width() > 500.0);
    assert!(h.ui.get_video_height() > 200.0);
    h.ui.window()
        .dispatch_event(WindowEvent::KeyPressed { text: " ".into() });
    h.ui.window()
        .dispatch_event(WindowEvent::KeyReleased { text: " ".into() });
    settle();
    assert_eq!(
        h.ui.get_panel_kind(),
        "gallery",
        "initial Space must not activate Close"
    );
    assert!(
        h.actions.borrow().contains(&"video-toggle".to_owned()),
        "Space pauses the newly opened video"
    );
    if h.qa {
        h.ui.set_selected_media_is_web_video(true);
        h.ui.set_media_provider_label("YouTube".into());
        h.capture("gallery-web-video", 1360, 850, 1.0);
        h.capture("gallery-web-video-small", 900, 500, 1.0);
        assert!(h.ui.get_show_web_video());
        assert!(
            !h.ui.get_show_video(),
            "web and mpv surfaces must not overlap"
        );
        assert!(
            ElementHandle::find_by_accessible_label(&h.ui, "Позиция видео")
                .next()
                .is_none(),
            "web player owns its controls"
        );
        h.click("Открыть на сайте");
        assert!(h.actions.borrow().contains(&"open-media-source".to_owned()));
        h.ui.set_web_video_status(
            "Не удалось открыть плеер. Можно посмотреть видео на сайте автора.".into(),
        );
        h.capture("gallery-web-video-fallback", 900, 500, 1.5);
        h.ui.set_selected_media_is_web_video(false);
    }
    h.ui.window().dispatch_event(WindowEvent::KeyPressed {
        text: Key::Escape.into(),
    });
    h.ui.window().dispatch_event(WindowEvent::KeyReleased {
        text: Key::Escape.into(),
    });
    settle();
    assert!(h.ui.get_panel_kind().is_empty(), "Escape closes the modal");
    h.panel("settings", "Настройки", "");
    if h.qa {
        resize(h.ui.window(), 1360, 850, 1.0);
        settle();
        h.click("Настройки загрузок");
        assert_eq!(h.ui.get_settings_section(), 1);
        let threads =
            ElementHandle::find_by_accessible_label(&h.ui, "Количество потоков скачивания")
                .find(|e| e.accessible_enabled().is_some())
                .unwrap();
        threads.set_accessible_value("8");
        let limit =
            ElementHandle::find_by_accessible_label(&h.ui, "Ограничить скорость скачивания")
                .find(|e| e.accessible_enabled() == Some(true))
                .unwrap();
        limit.invoke_accessible_default_action();
        settle();
        assert_eq!(
            h.ui.get_settings_speed(),
            0,
            "off means unlimited without entering zero"
        );
        limit.invoke_accessible_default_action();
        settle();
        assert_eq!(
            h.ui.get_settings_speed(),
            10,
            "toggle restores the last speed limit"
        );
        threads.scroll(0.0, -1000.0);
        settle();
        h.click("Сохранить");
        assert!(
            h.actions.borrow().contains(&"settings:8:10".to_string()),
            "custom numeric field preserves editable settings"
        );
        h.click("Основное");
        assert_eq!(h.ui.get_settings_section(), 0);
        assert_eq!(h.ui.get_settings_threads(), 8, "section changes keep edits");
        ElementHandle::find_by_element_id(&h.ui, "AppWindow::settings-scroll")
            .next()
            .unwrap()
            .scroll(0.0, 1000.0);
        settle();
        let input = ElementHandle::find_by_accessible_label(&h.ui, "Найти игру")
            .find(|e| e.accessible_enabled().is_some())
            .unwrap();
        assert_eq!(
            input.accessible_enabled(),
            Some(false),
            "modal disables background search"
        );
        let field = ElementHandle::find_by_accessible_label(&h.ui, "Папка с играми")
            .next()
            .unwrap();
        field.scroll(0.0, -1000.0);
        settle();
        h.capture("settings-scrolled", 1120, 720, 1.0);
        let save = ElementHandle::find_by_accessible_label(&h.ui, "Сохранить")
            .next()
            .unwrap();
        assert_in_bounds(&save, 1120.0, 720.0, "settings footer after scroll");
        h.capture("settings-small-scrolled", 900, 500, 1.0);
        let visible_field = ElementHandle::find_by_element_id(&h.ui, "AppWindow::settings-scroll")
            .next()
            .unwrap();
        visible_field.scroll(0.0, -1000.0);
        settle();
        h.capture("settings-small-scrolled", 900, 500, 1.0);
        let save = ElementHandle::find_by_accessible_label(&h.ui, "Сохранить")
            .next()
            .unwrap();
        assert_in_bounds(&save, 900.0, 500.0, "small settings footer after scroll");
        h.ui.set_settings_error("Папку игр и адрес сервера можно изменить после завершения загрузок и игр. Скорость, потоки и поведение окна можно менять сейчас. Исправьте путь или дождитесь окончания операции; введённые настройки сохранены на экране.".into());
        visible_field.scroll(0.0, -1000.0);
        settle();
        h.capture("settings-save-error", 900, 500, 1.0);
        h.ui.set_settings_error(
            "Выберите отдельную папку для игр, например D:\\Games\\ChillHub".into(),
        );
        visible_field.scroll(0.0, 1000.0);
        settle();
        let error_summary =
            ElementHandle::find_by_element_id(&h.ui, "AppWindow::settings-save-message")
                .next()
                .unwrap();
        assert_in_bounds(
            &error_summary,
            900.0,
            500.0,
            "settings error stays beside Save without scrolling",
        );
        h.capture("settings-path-error", 900, 500, 1.0);
        h.capture("settings-path-error", 1120, 720, 1.5);
        h.ui.set_settings_error("".into());
        h.panel("manage", "Управление игрой", "");
        resize(h.ui.window(), 1360, 850, 1.0);
        settle();
        let versions = ElementHandle::find_by_accessible_label(&h.ui, "Версия игры для установки")
            .next()
            .unwrap();
        versions.invoke_accessible_default_action();
        h.capture("version-menu-open", 1360, 850, 1.0);
        key(h.ui.window(), Key::DownArrow);
        key(h.ui.window(), Key::Return);
        assert_eq!(
            h.ui.get_panel_kind(),
            "manage",
            "Enter commits the choice without closing the parent modal"
        );
        assert!(
            h.actions.borrow().contains(&"version:2.6.A".to_owned()),
            "version dropdown supports keyboard selection"
        );
        h.ui.set_panel_kind("".into());
        settle();
        h.ui.set_versions(model(
            (0..12).map(|i| format!("release-{i}").into()).collect(),
        ));
        h.ui.set_selected_version("release-11".into());
        h.panel("manage", "Управление игрой", "");
        settle();
        for attempt in 0..2 {
            let versions =
                ElementHandle::find_by_accessible_label(&h.ui, "Версия игры для установки")
                    .next()
                    .unwrap();
            versions.invoke_accessible_default_action();
            settle();
            h.capture(&format!("version-menu-tail-{attempt}"), 1360, 850, 1.0);
            if attempt == 0 {
                key(h.ui.window(), Key::Escape);
                assert_eq!(h.ui.get_panel_kind(), "manage");
            } else {
                key(h.ui.window(), Key::UpArrow);
                key(h.ui.window(), Key::Return);
                assert!(
                    h.actions
                        .borrow()
                        .contains(&"version:release-10".to_owned()),
                    "reopened long menu preserves selection and keyboard navigation"
                );
            }
        }
        // Unlike Harness::panel, a real help action replaces modal content
        // without destroying the modal. A pointer click also moves focus into
        // the outgoing action, which an accessibility invocation does not do.
        h.panel("help", "Помощь", "Chill Hub");
        let feedback_action = ElementHandle::find_by_accessible_label(&h.ui, "Идея или ошибка")
            .next()
            .unwrap();
        let origin = feedback_action.absolute_position();
        let size = feedback_action.size();
        let position =
            slint::LogicalPosition::new(origin.x + size.width / 2.0, origin.y + size.height / 2.0);
        h.ui.window().dispatch_event(WindowEvent::PointerPressed {
            position,
            button: PointerEventButton::Left,
        });
        h.ui.window().dispatch_event(WindowEvent::PointerReleased {
            position,
            button: PointerEventButton::Left,
        });
        settle();
        assert_eq!(h.ui.get_panel_kind(), "feedback");
        key(h.ui.window(), Key::Escape);
        assert!(
            h.ui.get_panel_kind().is_empty(),
            "Escape closes feedback after a pointer transition from help"
        );
        h.panel("feedback", "Идея или ошибка", "");
        resize(h.ui.window(), 900, 500, 1.0);
        settle();
        let message = ElementHandle::find_by_accessible_label(&h.ui, "Текст сообщения")
            .next()
            .expect("required message visible before optional fields");
        assert_in_bounds(&message, 900.0, 500.0, "feedback message");
        let send = ElementHandle::find_by_accessible_label(&h.ui, "Отправить")
            .find(|e| e.accessible_enabled() == Some(true))
            .unwrap();
        assert_in_bounds(&send, 900.0, 500.0, "persistent feedback submit");
        h.capture("feedback-required-first", 900, 500, 1.0);
        resize(h.ui.window(), 1120, 720, 1.0);
        settle();
        h.click("Тема сообщения");
        h.click("Вопрос");
        ElementHandle::find_by_accessible_label(&h.ui, "Тема сообщения")
            .next()
            .unwrap()
            .scroll(0.0, -1000.0);
        settle();
        h.click("Отправить");
        assert!(
            h.actions.borrow().contains(&"feedback:question".to_owned()),
            "question remains a separate feedback category; no network is used"
        );
        ElementHandle::find_by_element_id(&h.ui, "AppWindow::feedback-scroll")
            .next()
            .unwrap()
            .scroll(0.0, -1000.0);
        settle();
        let input = ElementHandle::find_by_accessible_label(&h.ui, "Ваше имя, необязательно")
            .next()
            .unwrap();
        input.set_accessible_value("Игрок");
        settle();
        h.click("Закрыть");
        assert_eq!(h.ui.get_panel_kind(), "feedback");
        h.capture("feedback-unsaved", 1120, 720, 1.0);
        h.click("Продолжить ввод");
        let name = ElementHandle::find_by_accessible_label(&h.ui, "Ваше имя, необязательно")
            .next()
            .unwrap();
        assert_eq!(name.accessible_value().as_deref(), Some("Игрок"));
        h.panel("help", "Помощь", "Chill Hub");
        resize(h.ui.window(), 1360, 850, 1.0);
        settle();
        let before = h.ui.window().take_snapshot().unwrap();
        key(h.ui.window(), Key::Tab);
        let after = h.ui.window().take_snapshot().unwrap();
        assert!(
            before.as_bytes() == after.as_bytes(),
            "Tab changes keyboard focus without visual decoration"
        );
        h.panel("help", "Помощь", "Chill Hub");
        key(h.ui.window(), Key::Return);
        assert!(
            h.ui.get_panel_kind().is_empty(),
            "the focused close button still responds to Enter"
        );
    }
    h.ui.hide().unwrap();
    let auxiliary = AuxiliaryWindow::new().unwrap();
    let weak = auxiliary.as_weak();
    auxiliary.on_provider_changed(move |provider| {
        let ui = weak.upgrade().unwrap();
        ui.set_primary_text(
            if provider == 0 {
                "Добавить исключения"
            } else {
                "Открыть инструкцию"
            }
            .into(),
        );
    });
    auxiliary.show().unwrap();
    for (name, heading, details, busy) in [
        (
            "aux-update",
            "Обновляем Chill Hub",
            "Проверяем файлы обновления. Предыдущая версия сохранена для восстановления при ошибке. Игры и настройки находятся в отдельных папках и останутся на месте.",
            true,
        ),
        (
            "aux-security",
            "Исключения антивируса",
            "Антивирус перестанет проверять эти папки. Добавляйте только файлы, которым доверяете.",
            false,
        ),
        (
            "aux-error",
            "Обновление остановлено",
            "Предыдущая версия восстановлена. Игры и настройки остались на месте. После закрытия помощника можно повторно открыть Chill Hub.",
            false,
        ),
    ] {
        auxiliary.set_heading(heading.into());
        auxiliary.set_provider_visible(name == "aux-security");
        auxiliary.set_provider_names(model(vec![
            "Защитник Windows".into(),
            "Kaspersky".into(),
            "ESET".into(),
            "Avast".into(),
        ]));
        auxiliary.set_selected_provider(0);
        auxiliary.set_details(details.into());
        auxiliary.set_paths(
            if name == "aux-security" {
                "D:\\Games\\ChillHub\nC:\\Users\\Игрок\\AppData\\Local\\ChillHubUpdateStaging"
            } else if busy {
                "C:\\Users\\Игрок\\AppData\\Local\\ChillHub"
            } else {
                ""
            }
            .into(),
        );
        auxiliary.set_subtitle("Chill Hub".into());
        auxiliary.set_status(
            if name == "aux-error" {
                "Не удалось заменить файл: доступ запрещён. Возможно, его использует другой процесс. Закройте оставшиеся окна лаунчера и повторите обновление.\n\nЕсли ошибка повторится, откройте папку с журналами и приложите последний журнал к сообщению в поддержку. Состояние восстановления сохранено; повторное открытие лаунчера предложит следующий безопасный шаг."
            } else if busy {
                "Не закрывайте помощник до завершения."
            } else {
                "Изменения пока не внесены."
            }
            .into(),
        );
        auxiliary.set_busy(busy);
        auxiliary.set_error(name == "aux-error");
        auxiliary.set_progress_visible(busy);
        auxiliary.set_progress(0.62);
        auxiliary.set_primary_text(
            if name == "aux-error" {
                "Закрыть"
            } else if busy {
                "Обновляем…"
            } else {
                "Добавить исключения"
            }
            .into(),
        );
        auxiliary.set_secondary_text("Отмена".into());
        for (w, he, scale) in [(480, 420, 1.0), (640, 620, 1.5), (640, 620, 2.0)] {
            resize(auxiliary.window(), w, he, scale);
            settle();
            let snapshot = auxiliary.window().take_snapshot().unwrap();
            assert_eq!(snapshot.width(), (w as f32 * scale) as u32);
            if h.qa && busy {
                let progress = ElementHandle::find_by_element_id(
                    &auxiliary,
                    "AuxiliaryWindow::helper-progress",
                )
                .next()
                .expect("helper progress");
                assert_in_bounds(
                    &progress,
                    w as f32,
                    he as f32,
                    "helper progress cannot scroll away",
                );
                assert!(progress.absolute_position().y + progress.size().height < he as f32 - 76.0);
            }
            if h.qa {
                let filename = format!("{name}-{w}x{he}-{}pct.png", (scale * 100.0) as u32);
                image::save_buffer(
                    h.base.join("dist/qa-renders").join(&filename),
                    snapshot.as_bytes(),
                    snapshot.width(),
                    snapshot.height(),
                    image::ColorType::Rgba8,
                )
                .unwrap();
                h.renders.push(serde_json::json!({"file":filename,"logicalWidth":w,"logicalHeight":he,"scale":scale}));
            }
        }
        if h.qa && name == "aux-security" {
            resize(auxiliary.window(), 640, 620, 1.0);
            settle();
            let before = auxiliary.window().take_snapshot().unwrap();
            key(auxiliary.window(), Key::Tab);
            settle();
            let after = auxiliary.window().take_snapshot().unwrap();
            assert_eq!(
                before.as_bytes(),
                after.as_bytes(),
                "Auxiliary Tab must not add focus decoration"
            );
            let choice = ElementHandle::find_by_accessible_label(&auxiliary, "Выберите антивирус")
                .find(|e| e.accessible_enabled() == Some(true))
                .expect("antivirus selector");
            choice.invoke_accessible_default_action();
            settle();
            key(auxiliary.window(), Key::DownArrow);
            key(auxiliary.window(), Key::Return);
            assert_eq!(
                auxiliary.get_selected_provider(),
                1,
                "Antivirus choice remains keyboard-operable"
            );
            choice.invoke_accessible_default_action();
            settle();
            let snapshot = auxiliary.window().take_snapshot().unwrap();
            let filename = "aux-security-menu-640x620-100pct.png";
            image::save_buffer(
                h.base.join("dist/qa-renders").join(filename),
                snapshot.as_bytes(),
                snapshot.width(),
                snapshot.height(),
                image::ColorType::Rgba8,
            )
            .unwrap();
            h.renders.push(serde_json::json!({"file":filename,"logicalWidth":640,"logicalHeight":620,"scale":1.0}));
            key(auxiliary.window(), Key::Escape);
        }
    }
    auxiliary.hide().unwrap();
    if h.qa {
        h.ui.show().unwrap();
        h.panel("", "", "");
        h.ui.set_selected_game(0);
        resize(h.ui.window(), 1360, 850, 1.0);
        settle();
        let row = ElementHandle::find_by_accessible_label(&h.ui, "BOMBANANA!")
            .find(|e| e.accessible_enabled() == Some(true))
            .unwrap();
        let p = row.absolute_position();
        let s = row.size();
        let position = slint::LogicalPosition::new(p.x + s.width / 2.0, p.y + s.height / 2.0);
        h.ui.window().dispatch_event(WindowEvent::PointerPressed {
            position,
            button: PointerEventButton::Right,
        });
        h.ui.window().dispatch_event(WindowEvent::PointerReleased {
            position,
            button: PointerEventButton::Right,
        });
        settle();
        assert_eq!(h.ui.get_panel_kind(), "manage");
        assert_eq!(
            h.ui.get_selected_game(),
            1,
            "right click manages the clicked game"
        );
        key(h.ui.window(), Key::Escape);
        settle();
        for _ in 0..2 {
            h.ui.window().dispatch_event(WindowEvent::PointerPressed {
                position,
                button: PointerEventButton::Left,
            });
            h.ui.window().dispatch_event(WindowEvent::PointerReleased {
                position,
                button: PointerEventButton::Left,
            });
            mock_elapsed_time(Duration::from_millis(30));
        }
        assert_eq!(
            h.ui.get_panel_kind(),
            "manage",
            "double click opens details instead of launching a game"
        );
        h.ui.hide().unwrap();
    }
    let setup = SetupWindow::new().unwrap();
    setup.set_version(env!("CARGO_PKG_VERSION").into());
    setup.set_install_path("C:\\Users\\Игрок\\AppData\\Local\\Chill Hub".into());
    setup.set_games_path("D:\\Совместные игры\\Chill Hub".into());
    setup.set_needs_webview(true);
    setup.show().unwrap();
    for (name, removing, busy, done, error) in [
        ("setup-install", false, false, false, false),
        ("setup-downgrade", false, false, false, false),
        ("setup-progress", false, true, false, false),
        ("setup-error", false, false, false, true),
        ("setup-finished", false, false, true, false),
        ("setup-remove", true, false, false, false),
        ("setup-removed", true, false, true, false),
    ] {
        setup.set_removing(removing);
        setup.set_busy(busy);
        setup.set_done(done);
        setup.set_error(error);
        setup.set_downgrade_from(
            if name == "setup-downgrade" {
                "9.0.0"
            } else {
                ""
            }
            .into(),
        );
        setup.set_message(if error {"Операция не завершена: файл занят. Закройте лаунчер и повторите установку. Ваши игры сохранятся."} else if busy {"Подготавливаем файлы. Это может занять несколько минут."} else if done {"Операция завершена."} else {""}.into());
        for (w, he, scale) in [(560, 480, 1.0), (740, 690, 1.5), (740, 690, 2.0)] {
            resize(setup.window(), w, he, scale);
            settle();
            let snapshot = setup.window().take_snapshot().unwrap();
            assert_eq!(snapshot.width(), (w as f32 * scale) as u32);
            if h.qa {
                if busy {
                    let progress =
                        ElementHandle::find_by_element_id(&setup, "SetupWindow::setup-progress")
                            .next()
                            .expect("setup progress");
                    let p = progress.absolute_position();
                    let s = progress.size();
                    assert!(
                        p.y >= 90.0 && p.y + s.height < he as f32 - 120.0,
                        "setup progress must stay visible at {w}x{he}"
                    );
                }
                if error {
                    let status =
                        ElementHandle::find_by_element_id(&setup, "SetupWindow::status-copy")
                            .next()
                            .expect("setup error status");
                    let p = status.absolute_position();
                    let s = status.size();
                    assert!(
                        p.y >= 90.0 && s.height > 0.0 && p.y + s.height <= he as f32 - 80.0,
                        "setup error must remain visible above the footer at {w}x{he}"
                    );
                }
                let filename = format!("{name}-{w}x{he}-{}pct.png", (scale * 100.0) as u32);
                image::save_buffer(
                    h.base.join("dist/qa-renders").join(&filename),
                    snapshot.as_bytes(),
                    snapshot.width(),
                    snapshot.height(),
                    image::ColorType::Rgba8,
                )
                .unwrap();
                h.renders.push(serde_json::json!({"file":filename,"logicalWidth":w,"logicalHeight":he,"scale":scale}));
                for label in [
                    "Отмена",
                    "Закрыть",
                    "Установить",
                    "Удалить",
                    "Готово",
                    "Открыть Chill Hub",
                ] {
                    for button in ElementHandle::find_by_accessible_label(&setup, label)
                        .filter(|e| e.accessible_enabled() == Some(true))
                    {
                        let p = button.absolute_position();
                        let s = button.size();
                        assert!(
                            p.y >= 0.0 && p.y + s.height <= he as f32,
                            "{name}: footer outside {w}x{he}"
                        );
                    }
                }
            }
        }
    }
    setup.hide().unwrap();
    if h.qa {
        std::fs::write(
            h.base.join("dist/qa-renders/index.json"),
            serde_json::to_vec_pretty(&h.renders).unwrap(),
        )
        .unwrap();
        println!(
            "Rendered {} real UI states with headless software backend",
            h.renders.len()
        );
    }
}
