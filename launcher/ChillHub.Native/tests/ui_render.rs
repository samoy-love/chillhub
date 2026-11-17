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
    ComponentHandle, Image, LogicalSize, ModelRc, Rgba8Pixel, SharedPixelBuffer, SharedString,
    VecModel,
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
fn text_key(window: &slint::Window, text: &str) {
    window.dispatch_event(WindowEvent::KeyPressed { text: text.into() });
    window.dispatch_event(WindowEvent::KeyReleased { text: text.into() });
    settle();
}
fn shortcut(window: &slint::Window, text: &str) {
    window.dispatch_event(WindowEvent::KeyPressed {
        text: Key::Control.into(),
    });
    window.dispatch_event(WindowEvent::KeyPressed { text: text.into() });
    window.dispatch_event(WindowEvent::KeyReleased { text: text.into() });
    window.dispatch_event(WindowEvent::KeyReleased {
        text: Key::Control.into(),
    });
    settle();
}
fn click_input(window: &slint::Window, input: &ElementHandle) {
    let position = input.absolute_position();
    let size = input.size();
    let center = slint::LogicalPosition::new(
        position.x + size.width / 2.0,
        position.y + size.height / 2.0,
    );
    window.dispatch_event(WindowEvent::PointerPressed {
        position: center,
        button: PointerEventButton::Left,
    });
    window.dispatch_event(WindowEvent::PointerReleased {
        position: center,
        button: PointerEventButton::Left,
    });
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
                    status_progress: 0.,
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
        ui.set_ready_to_play(true);
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
        let hero_source = text(g, "Hero");
        let hero_path = if hero_source.ends_with("/hero.jpg") {
            format!("art/{id}/hero.webp")
        } else {
            hero_source.to_owned()
        };
        self.ui.set_hero(picture(&self.base, &hero_path));
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
    fn click_pointer(&self, label: &str) {
        let element = ElementHandle::find_by_accessible_label(&self.ui, label)
            .find(|e| e.accessible_enabled().unwrap_or(true))
            .unwrap_or_else(|| panic!("Missing enabled control: {label}"));
        click_input(self.ui.window(), &element);
    }
    fn capture(
        &mut self,
        name: &str,
        width: u32,
        height: u32,
        scale: f32,
    ) -> SharedPixelBuffer<Rgba8Pixel> {
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
            } else if self.ui.get_gallery_open() {
                "AppWindow::viewer"
            } else {
                "AppWindow::modal"
            };
            let container = ElementHandle::find_by_element_id(&self.ui, id)
                .next()
                .expect(id);
            assert_in_bounds(&container, width as f32, height as f32, name);
            if !self.ui.get_panel_kind().is_empty() {
                let close = if self.ui.get_gallery_open() {
                    ElementHandle::find_by_accessible_label(&self.ui, "Закрыть просмотр")
                        .next()
                        .unwrap()
                } else {
                    ElementHandle::find_by_element_id(&self.ui, "AppWindow::modal-close")
                        .next()
                        .unwrap()
                };
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
        snapshot
    }
    fn assert_install_estimate(&mut self, width: u32, height: u32, scale: f32) {
        const ACTION: &str = "Установить игру";
        const WARNING: &str = "Мало места";
        const HINT: &str = "Недостаточно места. Подготовить файлы: 43,2 ГБ · нужно места: 46,7 ГБ · свободно 19,1 ГБ";

        let previous_status = self.ui.get_status();
        let previous_label = self.ui.get_play_label();
        self.ui.set_status(ACTION.into());
        self.ui.set_play_label(ACTION.into());
        self.ui.set_install_hint(HINT.into());
        self.ui.set_install_space_status(WARNING.into());
        self.ui.set_install_space_low(true);
        let warning = self.capture("install-estimate", width, height, scale);
        assert!(self.ui.get_show_install_space_warning());
        assert_eq!(self.ui.get_game_status_text(), WARNING);
        assert_eq!(self.ui.get_shown_install_hint(), HINT);

        let dot = ElementHandle::find_by_element_id(&self.ui, "AppWindow::game-status-dot")
            .next()
            .expect("game status indicator");
        let hint = ElementHandle::find_by_element_id(&self.ui, "AppWindow::install-estimate-hint")
            .next()
            .expect("install estimate");
        let feature = ElementHandle::find_by_element_id(&self.ui, "AppWindow::feature")
            .next()
            .expect("game feature card");
        let button = ElementHandle::find_by_accessible_label(&self.ui, ACTION)
            .find(|element| element.accessible_enabled().is_some())
            .expect("install action");
        assert_eq!(button.accessible_enabled(), Some(true));
        assert_in_bounds(&dot, width as f32, height as f32, "install status");
        assert_in_bounds(&button, width as f32, height as f32, "install action");
        assert!(hint.absolute_position().y >= button.absolute_position().y + button.size().height);
        assert!(
            hint.absolute_position().y + hint.size().height
                <= feature.absolute_position().y + feature.size().height
        );
        let position = dot.absolute_position();
        let size = dot.size();
        let x = ((position.x + size.width / 2.0) * scale) as usize;
        let y = ((position.y + size.height / 2.0) * scale) as usize;
        let sample = |snapshot: &SharedPixelBuffer<Rgba8Pixel>| {
            let pixel = snapshot.as_slice()[y * snapshot.width() as usize + x];
            [pixel.r, pixel.g, pixel.b]
        };
        assert_eq!(
            sample(&warning),
            [0xd8, 0xb2, 0x7b],
            "warning status for {width}x{height}"
        );

        self.ui.set_install_hint("".into());
        self.ui.set_install_space_status("".into());
        self.ui.set_install_space_low(false);
        settle();
        let cleared = self.ui.window().take_snapshot().unwrap();
        assert!(!self.ui.get_show_install_space_warning());
        assert_eq!(self.ui.get_game_status_text(), ACTION);
        assert!(self.ui.get_shown_install_hint().is_empty());
        assert_eq!(
            sample(&cleared),
            [0xa7, 0xda, 0xba],
            "cleared status for {width}x{height}"
        );
        self.ui.set_status(previous_status);
        self.ui.set_play_label(previous_label);
    }
    fn assert_title_and_gutter(&self) {
        let title = ElementHandle::find_by_element_id(&self.ui, "AppWindow::hero-title")
            .next()
            .unwrap();
        let description =
            ElementHandle::find_by_element_id(&self.ui, "AppWindow::hero-description")
                .next()
                .unwrap();
        let feature = ElementHandle::find_by_element_id(&self.ui, "AppWindow::feature")
            .next()
            .unwrap();
        let launch_bar = ElementHandle::find_by_element_id(&self.ui, "AppWindow::launch-bar")
            .next()
            .unwrap();
        assert!(
            (title.absolute_position().y - feature.absolute_position().y - 64.0).abs() < 0.5,
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
        assert!(
            description.absolute_position().y + description.size().height + 20.0
                <= launch_bar.absolute_position().y,
            "the launch controls have a separate surface below the full description"
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

fn download_panel_height(ui: &AppWindow) -> f32 {
    ElementHandle::find_by_element_id(ui, "AppWindow::modal")
        .next()
        .expect("download panel")
        .size()
        .height
}

fn assert_download_numbers_fit(ui: &AppWindow) {
    let values: Vec<_> = ElementHandle::find_by_element_id(ui, "QueueMetric::value-copy").collect();
    assert_eq!(values.len(), 4);
    let mut values = values;
    values.sort_by(|left, right| {
        left.absolute_position()
            .x
            .total_cmp(&right.absolute_position().x)
    });
    let slot = values[1].absolute_position().x - values[0].absolute_position().x - 12.0;
    let total = ElementHandle::find_by_element_id(ui, "QueueMetric::note-copy")
        .next()
        .expect("total is displayed under the processed amount");
    assert!(slot > 0.0);
    assert_eq!(total.absolute_position().x, values[0].absolute_position().x);
    for value in values.iter().chain(std::iter::once(&total)) {
        assert!(
            value.size().width <= slot + 0.5,
            "counter of width {} overflows slot {slot}",
            value.size().width
        );
    }
}

#[test]
fn downloads_escape_after_focused_queue_actions_change() {
    use slint::Model;
    slint::platform::set_platform(Box::new(TestingBackend::new(TestingBackendOptions {
        mock_time: true,
        threading: false,
        renderer_name: Some("software".into()),
    })))
    .expect("headless platform only");
    let h = Harness::new();
    resize(h.ui.window(), 1120, 720, 1.0);
    for (label, action) in [
        ("Приостановить", "queue:active:pause"),
        ("Отменить", "queue:pending:cancel"),
    ] {
        let mut active = QueueRow {
            id: "active".into(),
            title: "Первая загрузка".into(),
            processing: true,
            can_pause: true,
            ..Default::default()
        };
        h.ui.set_queue_active(active.clone());
        h.ui.set_queue(model(vec![QueueRow {
            id: "pending".into(),
            title: "Вторая загрузка".into(),
            can_cancel: true,
            ..Default::default()
        }]));
        h.ui.set_queue_history(model(Vec::new()));
        h.panel("downloads", "Загрузки", "");
        h.ui.window().take_snapshot().expect("download panel");
        h.click_pointer(label);
        assert_eq!(h.actions.borrow_mut().pop().as_deref(), Some(action));

        active.progress = 0.8;
        h.ui.set_queue_active(active);
        settle();
        key(h.ui.window(), Key::Return);
        assert_eq!(
            h.actions.borrow_mut().pop().as_deref(),
            Some(action),
            "progress updates preserve the focused queue action"
        );

        h.ui.set_queue_active(QueueRow::default());
        h.ui.set_queue(model(Vec::new()));
        h.ui.set_queue_history(model(vec![QueueRow {
            id: "active".into(),
            title: "Первая загрузка".into(),
            state: "Готово".into(),
            can_remove: true,
            ..Default::default()
        }]));
        settle();
        key(h.ui.window(), Key::Escape);
        assert_eq!(h.ui.get_panel_kind(), "", "Escape after removing {label}");
    }

    h.ui.set_queue_active(QueueRow {
        id: "running".into(),
        title: "Активная загрузка".into(),
        ..Default::default()
    });
    let mut waiting = QueueRow {
        id: "waiting".into(),
        title: "Ожидающая загрузка".into(),
        can_pause: true,
        ..Default::default()
    };
    let pending = Rc::new(VecModel::from(vec![waiting.clone()]));
    h.ui.set_queue(ModelRc::from(pending.clone()));
    h.panel("downloads", "Загрузки", "");
    h.ui.window().take_snapshot().expect("download panel");
    h.click_pointer("Приостановить");
    assert_eq!(
        h.actions.borrow_mut().pop().as_deref(),
        Some("queue:waiting:pause")
    );
    waiting.can_pause = false;
    waiting.can_resume = true;
    pending.set_row_data(0, waiting);
    settle();
    key(h.ui.window(), Key::Escape);
    assert_eq!(
        h.ui.get_panel_kind(),
        "",
        "Escape after pausing a queued row"
    );

    let moved = QueueRow {
        id: "later".into(),
        title: "Следующая загрузка".into(),
        can_reorder: true,
        can_move_up: true,
        ..Default::default()
    };
    pending.push(moved.clone());
    h.panel("downloads", "Загрузки", "");
    h.ui.window().take_snapshot().expect("download panel");
    h.click_pointer("Поднять в очереди");
    assert_eq!(
        h.actions.borrow_mut().pop().as_deref(),
        Some("queue:later:move-up")
    );
    pending.remove(1);
    pending.insert(0, moved);
    settle();
    key(h.ui.window(), Key::Escape);
    assert_eq!(h.ui.get_panel_kind(), "", "Escape after reordering a row");
}

#[test]
fn disabled_controls_do_not_activate_after_focus() {
    slint::platform::set_platform(Box::new(TestingBackend::new(TestingBackendOptions {
        mock_time: true,
        threading: false,
        renderer_name: Some("software".into()),
    })))
    .expect("headless platform only");
    let mut h = Harness::new();
    h.game("lethal-company");
    h.ui.set_can_play(true);
    h.ui.set_play_label("Установить".into());
    let play_requests = Rc::new(std::cell::Cell::new(0));
    let requests = play_requests.clone();
    h.ui.on_play_requested(move || requests.set(requests.get() + 1));
    resize(h.ui.window(), 1120, 720, 1.0);
    h.ui.window().take_snapshot().unwrap();
    h.click_pointer("Установить");
    assert_eq!(play_requests.get(), 1);
    h.ui.set_can_play(false);
    settle();
    key(h.ui.window(), Key::Return);
    text_key(h.ui.window(), " ");
    assert_eq!(play_requests.get(), 1);

    let setup = SetupWindow::new().unwrap();
    let submits = Rc::new(std::cell::Cell::new(0));
    let requests = submits.clone();
    setup.on_submit(move || requests.set(requests.get() + 1));
    resize(setup.window(), 680, 560, 1.0);
    setup.window().take_snapshot().unwrap();
    let install = ElementHandle::find_by_accessible_label(&setup, "Установить")
        .find(|button| button.accessible_enabled() == Some(true))
        .expect("install button");
    click_input(setup.window(), &install);
    assert_eq!(submits.get(), 1);
    setup.set_busy(true);
    settle();
    key(setup.window(), Key::Return);
    text_key(setup.window(), " ");
    assert_eq!(submits.get(), 1);

    setup.set_busy(false);
    setup.window().take_snapshot().unwrap();
    let shortcut =
        ElementHandle::find_by_accessible_label(&setup, "Создать ярлык на рабочем столе")
            .find(|toggle| toggle.accessible_enabled() == Some(true))
            .expect("desktop shortcut toggle");
    assert!(setup.get_desktop_shortcut());
    click_input(setup.window(), &shortcut);
    assert!(!setup.get_desktop_shortcut());
    setup.set_picking(true);
    setup.set_busy(true);
    settle();
    key(setup.window(), Key::Return);
    text_key(setup.window(), " ");
    assert!(!setup.get_desktop_shortcut());
}

#[test]
fn preview_title_and_sparse_game_page_follow_their_bindings() {
    use slint::private_unstable_api::re_exports::WindowInner;

    slint::platform::set_platform(Box::new(TestingBackend::new(TestingBackendOptions {
        mock_time: true,
        threading: false,
        renderer_name: Some("software".into()),
    })))
    .expect("headless platform only");
    let h = Harness::new();
    let ui = &h.ui;
    let native_title = || {
        WindowInner::from_pub(ui.window())
            .window_item()
            .expect("Slint window item")
            .as_pin_ref()
            .title()
    };
    assert_eq!(native_title(), "Chill Hub");
    ui.set_window_title("Chill Hub · Проверочная копия".into());
    let cover = ui.get_hero();
    ui.set_game_title("A · Проверка очереди".into());
    ui.set_description("Игра из проверочного каталога.".into());
    ui.set_genre("".into());
    ui.set_hero(Image::default());
    ui.set_has_mods(false);
    ui.set_has_gallery(false);
    ui.set_media(model(Vec::new()));
    ui.set_highlights(model(Vec::new()));
    resize(ui.window(), 900, 500, 1.0);
    ui.hide().unwrap();
    ui.show().unwrap();
    settle();
    ui.window().take_snapshot().unwrap();
    assert_eq!(native_title(), "Chill Hub · Проверочная копия");
    assert!(
        ElementHandle::find_by_accessible_label(ui, "Обложка")
            .next()
            .is_none()
    );
    let details = || {
        ElementHandle::find_by_accessible_label(ui, "Об игре")
            .next()
            .map_or(0.0, |element| element.size().height)
    };
    assert_eq!(details(), 0.0);

    // A lone cover can still open from the gallery model without a decoded poster.
    ui.set_hero(cover);
    ui.set_media(model(vec![MediaTile {
        caption: "Изображение игры".into(),
        ..Default::default()
    }]));
    ui.set_highlights(model(vec!["Одна особенность игры".into()]));
    let opened = Rc::new(std::cell::Cell::new(None));
    let media_index = opened.clone();
    ui.on_media_selected(move |index| media_index.set(Some(index)));
    settle();
    ui.window().take_snapshot().unwrap();
    h.click_pointer("Обложка");
    assert_eq!(opened.get(), Some(0));
    assert!(details() > 0.0);
}

#[test]
fn real_ui_headless_layout_and_actions() {
    use slint::Model;
    slint::platform::set_platform(Box::new(TestingBackend::new(TestingBackendOptions {
        mock_time: true,
        threading: false,
        renderer_name: Some("software".into()),
    })))
    .expect("headless platform only");
    let mut h = Harness::new();
    // The selection marker must identify the clicked game on its first frame,
    // without passing over unrelated games during a long library jump.
    {
        let library = Harness::new();
        resize(library.ui.window(), 1360, 850, 1.0);
        library.ui.set_selected_game(0);
        settle();
        let last = library.ui.get_games().row_count() - 1;
        let marker_at = |index: usize| {
            let title = library.ui.get_games().row_data(index).unwrap().title;
            let row = ElementHandle::find_by_accessible_label(&library.ui, title.as_str())
                .find(|e| e.accessible_enabled().is_some())
                .unwrap();
            let position = row.absolute_position();
            ((position.x + 1.0) as usize, (position.y + 24.0) as usize)
        };
        let before = library.ui.window().take_snapshot().unwrap();
        let first_point = marker_at(0);
        let pixel = |image: &slint::SharedPixelBuffer<slint::Rgba8Pixel>, point: (usize, usize)| {
            image.as_slice()[point.1 * image.width() as usize + point.0]
        };
        let selected_color = pixel(&before, first_point);
        library.ui.invoke_game_selected(last as i32);
        let immediate = library.ui.window().take_snapshot().unwrap();
        assert_eq!(pixel(&immediate, marker_at(last)), selected_color);
        assert_ne!(pixel(&immediate, first_point), selected_color);
    }
    // Exercise the actual TextInput event path with EN and RU key text, including
    // Slint's clipboard-backed methods and ordinary character entry.
    {
        let keyboard = Harness::new();
        keyboard.panel("mods", "Состав модпака", "");
        keyboard.ui.set_mods(model(vec![]));
        let input = ElementHandle::find_by_accessible_label(
            &keyboard.ui,
            "Найти мод по названию или описанию",
        )
        .find(|element| element.accessible_enabled() == Some(true))
        .expect("mod search TextInput is available");
        for (select_all, copy, paste, cut, sample) in [
            ("a", "c", "v", "x", "моды EN"),
            ("ф", "с", "м", "ч", "моды RU"),
        ] {
            input.set_accessible_value(sample);
            click_input(keyboard.ui.window(), &input);
            shortcut(keyboard.ui.window(), select_all);
            shortcut(keyboard.ui.window(), copy);

            input.set_accessible_value("замена");
            click_input(keyboard.ui.window(), &input);
            shortcut(keyboard.ui.window(), select_all);
            shortcut(keyboard.ui.window(), paste);
            assert_eq!(
                input.accessible_value().as_deref(),
                Some(sample),
                "paste with {paste}"
            );

            input.set_accessible_value(sample);
            click_input(keyboard.ui.window(), &input);
            shortcut(keyboard.ui.window(), select_all);
            shortcut(keyboard.ui.window(), cut);
            assert_eq!(
                input.accessible_value().as_deref(),
                Some(""),
                "cut with {cut}"
            );
        }
        for (first, second, undo, redo) in [("a", "b", "z", "y"), ("ф", "и", "я", "н")] {
            input.set_accessible_value("");
            click_input(keyboard.ui.window(), &input);
            text_key(keyboard.ui.window(), first);
            text_key(keyboard.ui.window(), second);
            let expected = format!("{first}{second}");
            shortcut(keyboard.ui.window(), undo);
            assert_eq!(
                input.accessible_value().as_deref(),
                Some(""),
                "undo with {undo}"
            );
            shortcut(keyboard.ui.window(), redo);
            assert_eq!(
                input.accessible_value().as_deref(),
                Some(expected.as_str()),
                "redo with {redo}"
            );
        }
        for character in ["a", "ф"] {
            input.set_accessible_value("");
            click_input(keyboard.ui.window(), &input);
            text_key(keyboard.ui.window(), character);
            assert_eq!(
                input.accessible_value().as_deref(),
                Some(character),
                "plain {character} input"
            );
        }

        // Global shortcuts remain available under either layout and ignore Alt/Shift/Meta.
        keyboard.panel("", "", "");
        keyboard.ui.set_search("".into());
        let weak = keyboard.ui.as_weak();
        keyboard
            .ui
            .on_search_edited(move |value| weak.upgrade().unwrap().set_search(value));
        // Close the modal and explicitly materialize/focus a different live control.
        let game = ElementHandle::find_by_accessible_label(&keyboard.ui, "PEAK")
            .find(|e| e.accessible_enabled().is_some())
            .expect("library row is available");
        click_input(keyboard.ui.window(), &game);
        text_key(keyboard.ui.window(), "q");
        assert_eq!(
            keyboard.ui.get_search(),
            "",
            "typing outside the input leaves library search unchanged"
        );
        keyboard.actions.borrow_mut().clear();
        shortcut(keyboard.ui.window(), "r");
        assert_eq!(
            keyboard.actions.borrow().last().map(String::as_str),
            Some("retry-catalog")
        );
        keyboard.actions.borrow_mut().clear();
        shortcut(keyboard.ui.window(), "к");
        assert_eq!(
            keyboard.actions.borrow().last().map(String::as_str),
            Some("retry-catalog")
        );
        for (modifiers, key) in [
            ([Key::Control, Key::Alt], "r"),
            ([Key::Control, Key::Alt], "к"),
            ([Key::Control, Key::Shift], "r"),
            ([Key::Control, Key::Meta], "r"),
        ] {
            keyboard.actions.borrow_mut().clear();
            for modifier in modifiers {
                keyboard
                    .ui
                    .window()
                    .dispatch_event(WindowEvent::KeyPressed {
                        text: modifier.into(),
                    });
            }
            text_key(keyboard.ui.window(), key);
            for modifier in modifiers.into_iter().rev() {
                keyboard
                    .ui
                    .window()
                    .dispatch_event(WindowEvent::KeyReleased {
                        text: modifier.into(),
                    });
            }
            settle();
            assert_ne!(
                keyboard.actions.borrow().last().map(String::as_str),
                Some("retry-catalog"),
                "modified Ctrl+{key} is not consumed"
            );
        }

        keyboard.ui.set_search("".into());
        shortcut(keyboard.ui.window(), "k");
        text_key(keyboard.ui.window(), "e");
        assert_eq!(
            keyboard.ui.get_search(),
            "e",
            "Ctrl+K focuses library search in EN layout"
        );
        // Re-materialize the focus tree outside the input before the RU alias.
        click_input(keyboard.ui.window(), &game);
        text_key(keyboard.ui.window(), "q");
        assert_eq!(
            keyboard.ui.get_search(),
            "e",
            "typing outside the input leaves the existing search unchanged"
        );
        shortcut(keyboard.ui.window(), "л");
        text_key(keyboard.ui.window(), "ж");
        assert_eq!(
            keyboard.ui.get_search(),
            "eж",
            "Ctrl+Л focuses library search in RU layout"
        );
        keyboard.ui.hide().unwrap();
    }

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
        for (kind, icon) in [
            ("running", "play"),
            ("repair", "wrench"),
            ("interrupted", "pause"),
            ("queued", "clock"),
            ("active", "download"),
        ] {
            assert_eq!(h.ui.invoke_library_status_icon(kind.into()), icon);
        }
        let original_games = h.ui.get_games();
        let mut library_rows = slint::Model::iter(&original_games).collect::<Vec<_>>();
        for (row, (status, kind, progress)) in library_rows.iter_mut().zip([
            ("Установлена", "ready", 0.),
            ("Не установлена", "idle", 0.),
            ("Есть обновление", "update", 0.),
            ("Скачиваем · 49%", "active", 0.49),
            ("На паузе", "paused", 0.32),
            ("Ошибка загрузки", "error", 0.),
            ("В очереди", "queued", 0.),
            ("Восстанавливаем · 68%", "recovery", 0.68),
            ("", "", 0.),
        ]) {
            row.status = status.into();
            row.status_kind = kind.into();
            row.status_progress = progress;
        }
        h.ui.set_games(model(library_rows.clone()));
        h.capture("library-statuses", 1360, 850, 1.0);
        for row in library_rows {
            let entry = ElementHandle::find_by_accessible_label(&h.ui, row.title.as_str())
                .find(|e| e.accessible_enabled().is_some())
                .expect("game row");
            let status = if row.status.is_empty() {
                "Проверяем…"
            } else {
                row.status.as_str()
            };
            assert_eq!(entry.accessible_value().as_deref(), Some(status));
        }
        h.capture("library-statuses", 900, 650, 1.0);
        let mut activity_rows = slint::Model::iter(&original_games).collect::<Vec<_>>();
        for (row, (status, kind)) in activity_rows.iter_mut().zip([
            ("Игра запущена", "running"),
            ("Нужно восстановить", "repair"),
            ("Установка прервана", "interrupted"),
        ]) {
            row.status = status.into();
            row.status_kind = kind.into();
            row.status_progress = 0.;
        }
        h.ui.set_games(model(activity_rows));
        h.capture("library-availability", 1360, 850, 1.0);
        h.capture("library-availability", 900, 650, 1.0);
        let mut other_source_rows = slint::Model::iter(&original_games).collect::<Vec<_>>();
        let selected_row = &mut other_source_rows[h.ui.get_selected_game() as usize];
        selected_row.status = "Готова в Steam".into();
        selected_row.status_kind = "ready".into();
        h.ui.set_games(model(other_source_rows));
        let previous_play_label = h.ui.get_play_label();
        let previous_status = h.ui.get_status();
        h.ui.set_play_label("Установить".into());
        h.ui.set_status("Установить игру".into());
        h.ui.set_ready_to_play(false);
        h.capture("library-other-source", 1360, 850, 1.0);
        h.capture("library-other-source", 900, 650, 1.0);
        h.ui.set_play_label(previous_play_label);
        h.ui.set_status(previous_status);
        h.ui.set_ready_to_play(true);
        h.ui.set_games(original_games);
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
        h.assert_install_estimate(1707, 996, 1.5);
        h.assert_install_estimate(900, 500, 1.0);
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
        ("downloads", "Загрузки", ""),
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
    h.ui.set_queue_active(QueueRow {
        id: "running".into(),
        title: "PEAK".into(),
        icon: picture(&h.base, "Assets/Media/peak/icon.png"),
        operation: "Установка игры".into(),
        state: "Скачиваем".into(),
        file: "BepInEx/plugins/AdditionalContent/библиотека-модификаций.dll".into(),
        downloading: true,
        processing: true,
        processed: "4,2 ГБ".into(),
        processed_total: "8,6 ГБ".into(),
        transferred: "1,8 ГБ".into(),
        speed: "18,0 МБ/с".into(),
        eta: "≈ 4 мин".into(),
        progress: 0.49,
        progress_label: "49%".into(),
        can_pause: true,
        can_cancel: true,
        ..Default::default()
    });
    h.ui.set_queue(model(vec![
        QueueRow {
            id: "dependent".into(),
            title: "PEAK".into(),
            icon: picture(&h.base, "Assets/Media/peak/icon.png"),
            operation: "Модпак".into(),
            state: "После установки игры".into(),
            can_reorder: true,
            can_move_down: true,
            can_cancel: true,
            ..Default::default()
        },
        QueueRow {
            id: "waiting".into(),
            title: "Drive Beyond Horizons с длинным названием пакета".into(),
            icon: picture(&h.base, "Assets/Media/drive-beyond-horizons/icon.png"),
            operation: "Игра".into(),
            state: "В очереди".into(),
            can_reorder: true,
            can_move_up: true,
            can_move_down: true,
            can_cancel: true,
            ..Default::default()
        },
        QueueRow {
            id: "confirm".into(),
            title: "R.E.P.O.".into(),
            icon: picture(&h.base, "Assets/Media/repo/icon.png"),
            operation: "Обновление модпака".into(),
            state: "Требуется подтверждение".into(),
            detail: "Перед заменой файлов закройте игру и подтвердите продолжение.".into(),
            can_confirm: true,
            can_cancel: true,
            ..Default::default()
        },
    ]));
    h.ui.set_queue_history(model(vec![QueueRow {
        id: "failure".into(),
        title: "Lethal Company".into(),
        icon: picture(&h.base, "Assets/Media/lethal-company/icon.png"),
        operation: "Установка игры".into(),
        detail: "Соединение прервалось во время скачивания файла BepInEx/plugins/AdditionalContent/VeryLongRussianFileName/конфигурация-модификации.json.".into(),
        state: "Не удалось завершить".into(),
        can_resume: true,
        can_remove: true,
        ..Default::default()
    }]));
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
            let item = QueueRow {
                id: name.into(),
                title: "PEAK".into(),
                icon: picture(&h.base, "Assets/Media/peak/icon.png"),
                operation: "Обновление модпака".into(),
                state: state.into(),
                progress: 0.49,
                processing: matches!(name, "running" | "pausing" | "cancelling"),
                can_pause: actions[0],
                can_resume: actions[1],
                can_cancel: actions[2],
                can_confirm: actions[3],
                can_reorder: actions[4],
                can_move_up: name == "waiting",
                can_move_down: name == "paused",
                can_remove: actions[5],
                security_blocked: actions[6],
                ..Default::default()
            };
            let item = match name {
                "running" => QueueRow {
                    file: "BepInEx/plugins/AdditionalContent/библиотека-модификаций.dll".into(),
                    progress_label: "49%".into(),
                    downloading: true,
                    processed: "928,0 КБ".into(),
                    processed_total: "2,0 МБ".into(),
                    transferred: "1,8 ГБ".into(),
                    speed: "18,0 МБ/с".into(),
                    eta: "≈ 4 мин".into(),
                    ..item
                },
                "pausing" | "cancelling" => QueueRow {
                    detail: "Завершаем текущую операцию с файлом.".into(),
                    ..item
                },
                "failed" | "blocked" => QueueRow {
                    detail: "Не удалось получить файл. Проверьте подключение и повторите загрузку."
                        .into(),
                    ..item
                },
                "confirmation" => QueueRow {
                    detail: "В папке игры найдены файлы, которых нет в выбранной версии.".into(),
                    ..item
                },
                _ => item,
            };
            h.ui.set_queue_active(QueueRow::default());
            h.ui.set_queue(model(Vec::new()));
            h.ui.set_queue_history(model(Vec::new()));
            if item.can_remove {
                h.ui.set_queue_history(model(vec![item]));
            } else if name == "waiting" {
                h.ui.set_queue(model(vec![item]));
            } else {
                h.ui.set_queue_active(item);
            }
            for (w, height, scale) in [(900, 500, 1.0), (1120, 720, 2.0)] {
                h.capture(&format!("queue-{name}"), w, height, scale);
                if matches!(
                    name,
                    "running" | "pausing" | "paused" | "cancelling" | "confirmation"
                ) {
                    assert_eq!(download_panel_height(&h.ui), 320.0, "single {name} task");
                }
                if name == "running" {
                    assert_download_numbers_fit(&h.ui);
                }
                for (index, label) in [
                    "Приостановить",
                    if actions[5] {
                        "Повторить"
                    } else {
                        "Продолжить"
                    },
                    "Отменить",
                    "Подтвердить",
                    "Поднять в очереди",
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
                        let min_width = if index == 4 { 32.0 } else { 68.0 };
                        assert!(button.size().width >= min_width, "{name}: {label} hit area");
                        if index == 4 {
                            assert_eq!(button.accessible_enabled(), Some(name == "waiting"));
                            let down = ElementHandle::find_by_accessible_label(
                                &h.ui,
                                "Опустить в очереди",
                            )
                            .find(|e| e.accessible_enabled().is_some())
                            .expect("down reorder action");
                            assert_eq!(down.accessible_enabled(), Some(name == "paused"));
                            assert_in_bounds(&down, w as f32, height as f32, "down reorder action");
                        }
                    }
                }
            }
            if let Some((label, verb)) = match name {
                "waiting" => Some(("Поднять в очереди", "move-up")),
                "running" => Some(("Отменить", "cancel")),
                "paused" => Some(("Продолжить", "resume")),
                "failed" => Some(("Повторить", "resume")),
                "blocked" => Some(("Антивирус", "security")),
                "completed" => Some(("Убрать из списка", "remove")),
                "confirmation" => Some(("Подтвердить", "confirm")),
                _ => None,
            } {
                h.click_pointer(label);
                let expected = format!("queue:{name}:{verb}");
                assert!(
                    h.actions.borrow().contains(&expected),
                    "{label}: {expected}"
                );
            }
            if name == "paused" {
                h.click_pointer("Опустить в очереди");
                assert!(
                    h.actions
                        .borrow()
                        .contains(&"queue:paused:move-down".to_string())
                );
            }
            if name == "running" {
                let mut active = h.ui.get_queue_active();
                active.progress = 0.93;
                active.progress_label = "93%".into();
                active.processed = "17179869183,9 ГБ".into();
                active.processed_total = "17179869184,0 ГБ".into();
                active.transferred = "17179869183,9 ГБ".into();
                active.speed = "1023,9 МБ/с".into();
                active.eta = "≈ 486258747388 ч 59 мин".into();
                h.ui.set_queue_active(active);
                h.capture("queue-large-counters", 900, 500, 1.0);
                assert_download_numbers_fit(&h.ui);
                assert_eq!(download_panel_height(&h.ui), 320.0);

                h.ui.set_queue(model(vec![QueueRow {
                    id: "waiting".into(),
                    title: "Ожидающая загрузка".into(),
                    state: "В очереди".into(),
                    ..Default::default()
                }]));
                settle();
                assert_eq!(download_panel_height(&h.ui), 436.0, "active plus waiting");
                h.ui.set_queue(model(Vec::new()));
            }
        }
    }
    h.ui.set_queue_active(QueueRow::default());
    h.ui.set_queue(model(Vec::new()));
    if h.qa {
        for (count, expected_height) in [(0, 220.0), (1, 232.0), (2, 296.0), (12, 436.0)] {
            h.ui.set_queue_history(model(
                (0..count)
                    .map(|index| QueueRow {
                        id: format!("history-{index}").into(),
                        title: format!("Завершённая загрузка {}", index + 1).into(),
                        operation: "Установка игры".into(),
                        state: "Готово".into(),
                        can_remove: true,
                        ..Default::default()
                    })
                    .collect(),
            ));
            h.capture(&format!("queue-history-{count}"), 900, 500, 1.0);
            assert_eq!(
                download_panel_height(&h.ui),
                expected_height,
                "history with {count} records"
            );
        }
    }
    h.ui.set_queue_history(model(Vec::new()));
    h.capture("queue-empty", 1120, 720, 1.0);
    if h.qa {
        h.ui.set_queue_error("Не удалось прочитать сохранённую очередь: файл слишком большой. Исходная очередь оставлена на месте. Новые загрузки пока недоступны. Уже установленные игры и сохранения не изменены. Если ошибка повторится после перезапуска, приложите журнал к сообщению в поддержку.".into());
        h.capture("queue-storage-error", 900, 500, 1.0);
        assert!(
            ElementHandle::find_by_accessible_label(&h.ui, "Сбросить очередь")
                .next()
                .is_none()
        );
        h.ui.set_queue_error("Не удалось открыть сохранённую очередь загрузок.".into());
        h.ui.set_queue_recovery_needed(true);
        h.capture("queue-storage-recovery", 900, 500, 1.0);
        h.click("Сбросить очередь");
        assert!(
            h.actions
                .borrow()
                .iter()
                .any(|action| action == "queue::reset")
        );
        h.ui.set_queue_recovery_needed(false);
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
    let original_media = h.ui.get_media().iter().collect::<Vec<_>>();
    let image_index = original_media
        .iter()
        .position(|item| !item.is_video)
        .expect("fixture has an image card");
    let peak = h
        .catalog
        .iter()
        .find(|game| text(game, "Id") == "peak")
        .unwrap();
    let photo = &peak["Media"][image_index];
    let photo_path = text(photo, "Path");
    let (photo_width, photo_height) = image::image_dimensions(h.base.join(photo_path))
        .expect("first image media has a decoded source");
    h.ui.set_gallery_index(image_index as i32);
    h.ui.set_selected_media_is_video(false);
    h.ui.set_gallery_caption(text(photo, "Caption").into());
    h.ui.set_gallery_image(picture(&h.base, photo_path));
    h.ui.set_gallery_image_aspect(photo_width as f32 / photo_height.max(1) as f32);
    h.capture("gallery-image", 1120, 720, 2.0);
    assert!(
        h.ui.get_video_width() > 700.0,
        "viewer geometry spans the canvas stage"
    );
    let mut long_strip = original_media.clone();
    while long_strip.len() < 12 {
        long_strip.push(original_media[0].clone());
    }
    h.ui.set_media(model(long_strip));
    resize(h.ui.window(), 900, 500, 1.0);
    h.ui.set_gallery_index(11);
    settle();
    assert!(
        h.ui.get_viewer_strip_offset() < 0.0,
        "selected thumbnail scrolls into view when the strip overflows"
    );
    h.ui.set_gallery_index(0);
    h.ui.set_media(model(original_media.clone()));
    resize(h.ui.window(), 1120, 720, 2.0);
    settle();
    let photo_center = slint::LogicalPosition::new(
        h.ui.get_video_x() + h.ui.get_video_width() / 2.0,
        h.ui.get_video_y() + h.ui.get_video_height() / 2.0,
    );
    h.ui.window().dispatch_event(WindowEvent::PointerScrolled {
        position: photo_center,
        delta_x: 0.0,
        delta_y: 300.0,
    });
    settle();
    assert_eq!(h.ui.get_viewer_photo_zoom(), 1.5, "wheel-up zooms in");
    h.ui.window().dispatch_event(WindowEvent::PointerScrolled {
        position: photo_center,
        delta_x: 300.0,
        delta_y: 0.0,
    });
    settle();
    assert_eq!(
        h.ui.get_viewer_photo_zoom(),
        1.5,
        "horizontal wheel movement does not change photo scale"
    );
    h.ui.window().dispatch_event(WindowEvent::PointerScrolled {
        position: photo_center,
        delta_x: 0.0,
        delta_y: -300.0,
    });
    settle();
    assert_eq!(
        h.ui.get_viewer_photo_zoom(),
        1.0,
        "wheel-down returns to fit"
    );
    h.click("Увеличить изображение");
    assert_eq!(h.ui.get_viewer_photo_zoom(), 1.5);
    h.ui.window().dispatch_event(WindowEvent::PointerMoved {
        position: photo_center,
    });
    settle();
    assert_eq!(
        h.ui.get_viewer_photo_pan_y(),
        0.0,
        "hover does not drag a zoomed photo"
    );
    h.ui.window().dispatch_event(WindowEvent::PointerPressed {
        position: photo_center,
        button: PointerEventButton::Left,
    });
    h.ui.window().dispatch_event(WindowEvent::PointerMoved {
        position: slint::LogicalPosition::new(photo_center.x, photo_center.y + 80.0),
    });
    h.ui.window().dispatch_event(WindowEvent::PointerReleased {
        position: slint::LogicalPosition::new(photo_center.x, photo_center.y + 80.0),
        button: PointerEventButton::Left,
    });
    settle();
    assert!(
        h.ui.get_viewer_photo_pan_y() > 0.0,
        "drag pans the zoomed photo"
    );
    h.click("Вписать изображение");
    assert_eq!(h.ui.get_viewer_photo_zoom(), 1.0);
    assert_eq!(
        h.ui.get_viewer_photo_pan_y(),
        0.0,
        "fit recenters a previously panned photo"
    );
    for _ in 0..2 {
        h.ui.window().dispatch_event(WindowEvent::PointerPressed {
            position: photo_center,
            button: PointerEventButton::Left,
        });
        h.ui.window().dispatch_event(WindowEvent::PointerReleased {
            position: photo_center,
            button: PointerEventButton::Left,
        });
        mock_elapsed_time(Duration::from_millis(50));
    }
    assert!(
        h.ui.get_viewer_fullscreen(),
        "double-clicking a photo enters fullscreen"
    );
    key(h.ui.window(), Key::Escape);
    assert!(!h.ui.get_viewer_fullscreen());
    h.ui.set_gallery_image_state("error".into());
    let retries = Rc::new(RefCell::new(0));
    let retry_count = retries.clone();
    h.ui.on_media_retry(move || *retry_count.borrow_mut() += 1);
    h.click("Попробовать ещё раз");
    assert_eq!(
        *retries.borrow(),
        1,
        "image failure exposes a working retry action"
    );
    h.ui.set_gallery_image_state("ready".into());
    h.click("Скрыть ленту материалов");
    assert!(!h.ui.get_viewer_filmstrip_open());
    h.click("Показать ленту материалов");
    assert!(h.ui.get_viewer_filmstrip_open());
    h.ui.set_selected_media_is_video(true);
    h.ui.set_video_duration(103.0);
    h.ui.set_video_position(37.0);
    h.ui.set_video_time_label("0:37 / 1:43".into());
    h.ui.set_video_qualities(model(vec![480, 720, 1080]));
    h.ui.set_video_quality(1080);
    h.capture("gallery-video-controls", 1360, 850, 1.0);
    if h.qa {
        h.capture("gallery-video-small", 900, 500, 1.0);
        let selected = Rc::new(RefCell::new(0));
        let received = selected.clone();
        h.ui.on_video_quality_selected(move |height| *received.borrow_mut() = height);
        let stage_bottom = h.ui.get_video_y() + h.ui.get_video_height();
        h.click_pointer("Выбрать качество видео");
        let quality = ElementHandle::find_by_accessible_label(&h.ui, "720p")
            .find(|element| element.accessible_enabled() == Some(true))
            .unwrap();
        assert!(
            quality.absolute_position().y >= stage_bottom,
            "quality options stay in the footer below the native video stage"
        );
        key(h.ui.window(), Key::Escape);
        assert!(
            !h.ui.get_viewer_quality_open() && h.ui.get_gallery_open(),
            "Escape closes the quality picker before the viewer"
        );
        h.click_pointer("Выбрать качество видео");
        h.click_pointer("720p");
        assert_eq!(*selected.borrow(), 720);
        text_key(h.ui.window(), "f");
        assert!(
            h.ui.get_viewer_fullscreen(),
            "keyboard fullscreen shortcut remains active after changing video quality"
        );
        key(h.ui.window(), Key::Escape);
        assert!(!h.ui.get_viewer_fullscreen());
        key(h.ui.window(), Key::F11);
        assert!(
            h.ui.get_viewer_fullscreen(),
            "F11 remains active after the quality change and fullscreen round trip"
        );
        key(h.ui.window(), Key::Escape);
        assert!(!h.ui.get_viewer_fullscreen());
        assert_eq!(
            h.ui.get_video_y() + h.ui.get_video_height(),
            stage_bottom,
            "opening and selecting quality does not resize the native video surface"
        );
        h.ui.set_video_failed(true);
        h.ui.set_video_time_label("Ошибка видео".into());
        h.capture("gallery-video-error", 900, 500, 1.0);
        h.click_pointer("Выбрать качество видео");
        h.click_pointer("480p");
        assert_eq!(
            *selected.borrow(),
            480,
            "quality remains actionable after an error"
        );
        h.ui.set_video_failed(false);
        h.ui.set_video_time_label("0:37 / 1:43".into());
        let media = h.ui.get_media().iter().collect::<Vec<_>>();
        let video_index = media
            .iter()
            .position(|item| item.is_video)
            .expect("fixture has video media");
        let image_index = media
            .iter()
            .position(|item| !item.is_video)
            .expect("fixture has image media");
        h.ui.set_gallery_index(video_index as i32);
        h.ui.set_selected_media_is_video(true);
        h.ui.set_viewer_quality_open(true);
        h.ui.set_gallery_index(image_index as i32);
        h.ui.set_selected_media_is_video(false);
        settle();
        assert!(
            !h.ui.get_viewer_quality_open(),
            "changing media closes the quality picker"
        );
        h.ui.set_gallery_index(video_index as i32);
        h.ui.set_selected_media_is_video(true);
    }
    // The child HWND video surface is intentionally not created in a UI test.
    assert!(h.ui.get_show_video());
    assert!(h.ui.get_video_width() > 500.0);
    assert!(
        h.ui.get_video_height() > 200.0,
        "small viewer retains a useful video stage"
    );
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
    let seeks = Rc::new(RefCell::new(Vec::new()));
    let seek_values = seeks.clone();
    h.ui.on_video_seek(move |seconds| seek_values.borrow_mut().push(seconds));
    let steps = Rc::new(RefCell::new(Vec::new()));
    let gallery_steps = steps.clone();
    h.ui.on_gallery_step(move |delta| gallery_steps.borrow_mut().push(delta));
    h.ui.set_selected_media_is_video(false);
    key(h.ui.window(), Key::RightArrow);
    assert_eq!(
        steps.borrow().last().copied(),
        Some(1),
        "photo arrows navigate gallery material"
    );
    h.ui.set_selected_media_is_video(true);
    let mutes = Rc::new(RefCell::new(0));
    let mute_count = mutes.clone();
    h.ui.on_video_mute_toggle(move || *mute_count.borrow_mut() += 1);
    text_key(h.ui.window(), "ь");
    assert_eq!(
        *mutes.borrow(),
        1,
        "Russian layout mute shortcut is handled"
    );
    h.ui.set_video_position(37.0);
    key(h.ui.window(), Key::LeftArrow);
    assert_eq!(
        seeks.borrow().last().copied(),
        Some(32.0),
        "video arrows seek by five seconds"
    );
    h.ui.window().dispatch_event(WindowEvent::KeyPressed {
        text: Key::Shift.into(),
    });
    key(h.ui.window(), Key::RightArrow);
    h.ui.window().dispatch_event(WindowEvent::KeyReleased {
        text: Key::Shift.into(),
    });
    assert_eq!(
        steps.borrow().last().copied(),
        Some(1),
        "Shift+arrow navigates the gallery"
    );
    let fullscreen = Rc::new(RefCell::new(Vec::new()));
    let fullscreen_changes = fullscreen.clone();
    h.ui.on_viewer_fullscreen_changed(move |value| fullscreen_changes.borrow_mut().push(value));
    text_key(h.ui.window(), "F");
    assert!(h.ui.get_viewer_fullscreen());
    assert!(
        !h.ui.get_viewer_filmstrip_open(),
        "fullscreen prioritizes the media stage"
    );
    key(h.ui.window(), Key::Escape);
    assert!(!h.ui.get_viewer_fullscreen());
    assert!(
        h.ui.get_viewer_filmstrip_open(),
        "leaving fullscreen restores the normal filmstrip state"
    );
    assert_eq!(
        h.ui.get_panel_kind(),
        "gallery",
        "Escape exits fullscreen before closing viewer"
    );
    assert_eq!(fullscreen.borrow().as_slice(), &[true, false]);
    let photo_index = original_media
        .iter()
        .position(|item| !item.is_video)
        .expect("fixture has a photo after video");
    h.ui.set_gallery_index(photo_index as i32);
    h.ui.set_selected_media_is_video(false);
    h.ui.set_viewer_photo_zoom(1.5);
    settle();
    let drag_center = slint::LogicalPosition::new(
        h.ui.get_video_x() + h.ui.get_video_width() / 2.0,
        h.ui.get_video_y() + h.ui.get_video_height() / 2.0,
    );
    h.ui.window().dispatch_event(WindowEvent::PointerPressed {
        position: drag_center,
        button: PointerEventButton::Left,
    });
    h.ui.window().dispatch_event(WindowEvent::PointerMoved {
        position: slint::LogicalPosition::new(drag_center.x, drag_center.y + 30.0),
    });
    h.ui.window().dispatch_event(WindowEvent::PointerReleased {
        position: slint::LogicalPosition::new(drag_center.x, drag_center.y + 30.0),
        button: PointerEventButton::Left,
    });
    settle();
    key(h.ui.window(), Key::Escape);
    assert!(
        h.ui.get_panel_kind().is_empty(),
        "Escape closes the viewer after switching video to a dragged photo"
    );
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
    let helper_closes = Rc::new(RefCell::new(0));
    let closes = helper_closes.clone();
    auxiliary.on_close_requested(move || *closes.borrow_mut() += 1);
    auxiliary.show().unwrap();
    settle();
    key(auxiliary.window(), Key::Escape);
    assert_eq!(
        *helper_closes.borrow(),
        1,
        "Escape works before the first click or Tab in a helper"
    );
    auxiliary.set_busy(true);
    key(auxiliary.window(), Key::Escape);
    assert_eq!(
        *helper_closes.borrow(),
        1,
        "Escape cannot interrupt a busy helper"
    );
    auxiliary.set_busy(false);
    auxiliary.set_can_close(false);
    key(auxiliary.window(), Key::Escape);
    assert_eq!(
        *helper_closes.borrow(),
        1,
        "Escape respects the helper close guard"
    );
    auxiliary.set_can_close(true);
    let copied_folder = Rc::new(std::cell::Cell::new(-1));
    let folder_callback = copied_folder.clone();
    auxiliary.on_copy_folder(move |index| folder_callback.set(index));
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
            "aux-security-other",
            "Исключения антивируса",
            "Откройте настройки вашего антивируса и найдите раздел исключений. Скопируйте нужные пути из списка.",
            false,
        ),
        (
            "aux-security-remove",
            "Исключения антивируса",
            "Удалим только исключения Chill Hub. Остальные записи не изменятся.",
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
        let other = name == "aux-security-other";
        let removing = name == "aux-security-remove";
        auxiliary.set_provider_visible(name == "aux-security" || other);
        auxiliary.set_owned_removal_available(name == "aux-security" || other);
        auxiliary.set_provider_names(model(vec![
            "Защитник Windows".into(),
            "Kaspersky".into(),
            "ESET".into(),
            "Avast".into(),
            "Другой антивирус".into(),
        ]));
        auxiliary.set_selected_provider(if other { 4 } else { 0 });
        auxiliary.set_folders(model(if name == "aux-security" || other {
            vec![
                r"D:\Games\ChillHub".into(),
                r"C:\Users\Игрок\AppData\Local\ChillHubUpdateStaging".into(),
            ]
        } else {
            Vec::new()
        }));
        auxiliary.set_selected_folder(0);
        auxiliary.set_details(details.into());
        auxiliary.set_paths(
            if name == "aux-security" || removing {
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
            } else if other {
                ""
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
            if other {
                ""
            } else if removing {
                "Удалить исключения"
            } else if name == "aux-error" {
                "Закрыть"
            } else if busy {
                "Обновляем…"
            } else {
                "Добавить исключения"
            }
            .into(),
        );
        auxiliary.set_secondary_text(
            if other {
                "Закрыть"
            } else if removing {
                "Назад"
            } else {
                "Отмена"
            }
            .into(),
        );
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
        if h.qa && other {
            resize(auxiliary.window(), 480, 420, 1.0);
            settle();
            assert!(
                ElementHandle::find_by_accessible_label(&auxiliary, "Открыть инструкцию")
                    .next()
                    .is_none()
            );
            let choice = ElementHandle::find_by_accessible_label(&auxiliary, "Выберите папку")
                .find(|e| e.accessible_enabled() == Some(true))
                .expect("folder selector");
            choice.invoke_accessible_default_action();
            settle();
            key(auxiliary.window(), Key::DownArrow);
            key(auxiliary.window(), Key::Return);
            let copy = ElementHandle::find_by_accessible_label(&auxiliary, "Копировать")
                .find(|e| e.accessible_enabled() == Some(true))
                .expect("copy chosen folder");
            assert_in_bounds(&copy, 480., 420., "copy chosen folder");
            click_input(auxiliary.window(), &copy);
            assert_eq!(copied_folder.get(), 1);
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
    setup.show().unwrap();
    let setup_cancels = Rc::new(RefCell::new(0));
    let cancels = setup_cancels.clone();
    setup.on_cancel(move || *cancels.borrow_mut() += 1);
    settle();
    key(setup.window(), Key::Escape);
    assert_eq!(
        *setup_cancels.borrow(),
        1,
        "Escape works before the first click or Tab in setup"
    );
    setup.set_busy(true);
    key(setup.window(), Key::Escape);
    assert_eq!(
        *setup_cancels.borrow(),
        1,
        "Escape cannot interrupt setup while files are changing"
    );
    setup.set_busy(false);
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
        for (w, he, scale) in [
            (560, 480, 1.0),
            (680, 560, 1.0),
            (740, 690, 1.5),
            (740, 690, 2.0),
        ] {
            resize(setup.window(), w, he, scale);
            settle();
            let snapshot = setup.window().take_snapshot().unwrap();
            assert_eq!(snapshot.width(), (w as f32 * scale) as u32);
            if h.qa {
                if name == "setup-install" {
                    let scroll =
                        ElementHandle::find_by_element_id(&setup, "SetupWindow::body-scroll")
                            .next()
                            .expect("setup form scroll area");
                    let shortcut = ElementHandle::find_by_accessible_label(
                        &setup,
                        "Создать ярлык на рабочем столе",
                    )
                    .find(|e| e.accessible_enabled() == Some(true))
                    .expect("setup shortcut checkbox");
                    assert!(
                        shortcut.absolute_position().y + shortcut.size().height
                            <= scroll.absolute_position().y + scroll.size().height + 0.5,
                        "shortcut checkbox is visible without scrolling at {w}x{he}"
                    );
                }
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
