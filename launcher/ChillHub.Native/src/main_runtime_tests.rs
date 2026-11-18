use super::*;
use catalog::{Media, ModEntry};
use chillhub_native::{config::ConfigStore, model::VideoVariant, paths::AppPaths};
use std::{cell::Cell, fs, time::Instant};

#[test]
fn startup_resolves_installation_roots_without_reading_the_current_directory() {
    let temp = tempfile::tempdir().unwrap();
    let exe = temp.path().join("ChillHub.exe");
    let missing = roots(&exe);
    #[cfg(not(feature = "benchmark"))]
    assert!(missing.unwrap_err().contains("Переустановите"));
    #[cfg(feature = "benchmark")]
    assert_eq!(missing.unwrap().0, Path::new(env!("CARGO_MANIFEST_DIR")));

    fs::create_dir(temp.path().join("Data")).unwrap();
    fs::write(temp.path().join("Data/games.json"), "[]").unwrap();
    assert_eq!(
        roots(&exe).unwrap(),
        (temp.path().to_owned(), temp.path().join("art"))
    );
}

#[test]
fn startup_rejects_missing_renderer_and_relative_preview_profile() {
    fn args(values: &[&str]) -> Vec<String> {
        values.iter().map(ToString::to_string).collect()
    }
    assert_eq!(renderer_from_args(&[]).unwrap(), "software");
    assert_eq!(
        renderer_from_args(&args(&["--renderer", "femtovg-wgpu"])).unwrap(),
        "femtovg-wgpu"
    );
    assert!(renderer_from_args(&args(&["--renderer", "unknown"])).is_err());
    assert!(renderer_from_args(&args(&["--renderer"])).is_err());
    assert!(renderer_from_args(&args(&["--renderer", "--game"])).is_err());
    assert_eq!(
        renderer_from_args(&args(&["--renderer", "femtovg"])).is_ok(),
        cfg!(feature = "benchmark")
    );
    assert_eq!(preview_directory(&[]).unwrap(), None);
    assert!(preview_directory(&args(&["--preview-directory"])).is_err());
    assert!(preview_directory(&args(&["--preview-directory", "relative"])).is_err());
    let temp = tempfile::tempdir().unwrap();
    assert_eq!(
        preview_directory(&args(&[
            "--preview-directory",
            temp.path().to_str().unwrap(),
            "--renderer",
            "software",
        ]))
        .unwrap(),
        Some(temp.path())
    );
}

pub(super) fn run(base: &str) {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let config = AppConfig {
        api_base_url: base.into(),
        games_path: paths.games_dir.to_string_lossy().into_owned(),
        last_seen_changelog_version: env!("CARGO_PKG_VERSION").into(),
        ..Default::default()
    };
    ConfigStore::new(paths.clone()).save(&config).unwrap();
    let (services, config) = Services::new(paths.clone()).unwrap();
    assert_eq!(services.paths.games_dir, paths.games_dir);
    let art = temp.path().join("art");
    fs::create_dir(&art).unwrap();
    image::RgbImage::from_pixel(6, 3, image::Rgb([74, 38, 193]))
        .save(art.join("photo.png"))
        .unwrap();
    image::RgbImage::from_pixel(9, 3, image::Rgb([38, 120, 74]))
        .save(art.join("plain.png"))
        .unwrap();
    fs::write(art.join("damaged.png"), "corrupt").unwrap();
    let mut app = AppState::new(services, config, temp.path().into(), art, vec![], None);
    app.catalog = vec![
        Game {
            id: "plain".into(),
            title: "Обычная игра".into(),
            hero: "art/plain.png".into(),
            local_supported: true,
            ..Default::default()
        },
        Game {
            id: "modded".into(),
            title: "Игра с модами".into(),
            hero: "art/photo.png".into(),
            local_supported: true,
            steam_supported: true,
            has_mods: true,
            modpack_name: "Модпак".into(),
            modpack_version: "1.2".into(),
            metadata_note: "Примечание к сборке".into(),
            mods: vec![
                ModEntry {
                    name: "Compass".into(),
                    russian_name: "Компас".into(),
                    category: "Интерфейс".into(),
                    description: "Показывает направление".into(),
                    version: "1.1".into(),
                },
                ModEntry {
                    name: "Maps".into(),
                    category: "Интерфейс".into(),
                    ..Default::default()
                },
                ModEntry {
                    name: "Fog".into(),
                    category: "Графика".into(),
                    ..Default::default()
                },
            ],
            media: vec![
                Media {
                    kind: "image".into(),
                    path: "art/photo.png".into(),
                    caption: "Снимок".into(),
                    ..Default::default()
                },
                Media {
                    kind: "image".into(),
                    path: "art/damaged.png".into(),
                    caption: "Повреждённый файл".into(),
                    ..Default::default()
                },
                Media {
                    kind: "video".into(),
                    path: "art/photo.png".into(),
                    caption: "Трейлер".into(),
                    variants: [1080, 480, 720, 720]
                        .into_iter()
                        .map(|height| VideoVariant {
                            height,
                            url: format!("/assets/clip-{height}.mp4"),
                            ..Default::default()
                        })
                        .collect(),
                    ..Default::default()
                },
                Media {
                    kind: "video".into(),
                    caption: "Недоступный трейлер".into(),
                    url: "https://example.invalid/assets/clip.mp4".into(),
                    ..Default::default()
                },
            ],
            ..Default::default()
        },
    ];
    app.icons = vec![Image::default(); app.catalog.len()];
    app.library_status
        .insert("modded".into(), ("Есть обновление".into(), "active".into()));
    let state = Rc::new(RefCell::new(app));
    ACTIVE_STATE.with(|slot| assert!(slot.borrow_mut().replace(state.clone()).is_none()));
    let ui = AppWindow::new().unwrap();
    bind(&ui, &state);
    ui.show().unwrap();
    library_and_panels(&ui, &state);
    gallery(&ui, &state);
    hero_switch(&ui, &state);
    close_panel(&ui, &state);
    state.borrow().services.queue.shutdown();
    state.borrow().toast_timer.stop();
    ui.hide().unwrap();
    ACTIVE_STATE.with(|slot| *slot.borrow_mut() = None);
}

fn library_and_panels(ui: &AppWindow, state: &State) {
    refresh_library(ui, state);
    assert_eq!(ui.get_games().row_count(), 2);
    ui.invoke_game_selected(0);
    assert_eq!(ui.get_game_title(), "Обычная игра");
    ui.invoke_search_edited("невозможный поиск".into());
    assert_eq!(ui.get_games().row_count(), 0);
    assert_eq!(ui.get_selected_game(), -1);
    assert_eq!(ui.get_game_title(), "Обычная игра");
    ui.invoke_game_selected(99);
    assert_eq!(state.borrow().selected, 0);
    ui.invoke_search_edited("".into());
    ui.invoke_search_edited("Игра с модами".into());
    ui.invoke_game_selected(0);
    assert_eq!(state.borrow().selected, 1);
    assert_eq!(ui.get_selected_game(), 0);
    let game = ui.get_games().row_data(0).unwrap();
    assert_eq!(game.id, "modded");
    assert_eq!(game.status, "Есть обновление");
    assert_eq!(ui.get_game_title(), "Игра с модами");
    assert!(ui.get_has_gallery());
    assert_eq!(ui.get_media().row_count(), 4);
    assert_eq!(ui.get_media().row_data(2).unwrap().caption, "Трейлер");

    ui.invoke_source_selected("steam".into());
    assert_eq!(
        state.borrow().choices["modded"],
        ("steam".into(), "plain".into())
    );
    ui.invoke_mode_selected("mods".into());
    ui.invoke_source_selected("steam".into());
    assert_eq!(
        state.borrow().choices["modded"],
        ("steam".into(), "mods".into())
    );

    ui.invoke_panel_requested("mods".into());
    assert_eq!(ui.get_panel_title(), "Модпак");
    assert_eq!(ui.get_panel_subtitle(), "Версия 1.2 · 3 модов");
    assert_eq!(ui.get_mods().row_count(), 3);
    assert_eq!(ui.get_mod_categories().row_count(), 3);
    assert_eq!(ui.get_mods().row_data(0).unwrap().name, "Компас · Compass");
    ui.invoke_mod_category_selected("Интерфейс".into());
    assert_eq!(ui.get_mods().row_count(), 2);
    ui.invoke_mod_search_edited("НАПРАВЛЕНИЕ".into());
    assert_eq!(ui.get_mods().row_count(), 1);
    assert_eq!(ui.get_mods().row_data(0).unwrap().version, "1.1");
    ui.invoke_mod_category_selected("Графика".into());
    assert_eq!(ui.get_mods().row_count(), 0);
    ui.invoke_panel_closed();
    assert!(ui.get_panel_kind().is_empty());
    ui.invoke_panel_requested("mods".into());
    assert_eq!(ui.get_mod_category(), "Все");
    assert_eq!(ui.get_mods().row_count(), 3);

    for (kind, title) in [
        ("downloads", "Загрузки"),
        ("settings", "Настройки"),
        ("security", "Антивирус"),
        ("manage", "Управление · Игра с модами"),
        ("feedback", "Идея или ошибка"),
        ("changelog", "Что нового"),
        ("about", "Chill Hub"),
    ] {
        ui.invoke_panel_requested(kind.into());
        assert_eq!(ui.get_panel_kind(), kind);
        assert_eq!(ui.get_panel_title(), title);
    }
    assert!(ui.get_releases().row_count() > 0);
    wait_for(ui, |ui| {
        ui.get_hero().size().width == 6
            && ui.get_media().row_data(0).unwrap().poster.size().width == 6
    });
}

fn gallery(ui: &AppWindow, state: &State) {
    ui.invoke_media_selected(0);
    assert_eq!(ui.get_gallery_image_state(), "loading");
    assert_eq!(ui.get_gallery_caption(), "Снимок");
    wait_for(ui, |ui| ui.get_gallery_image_state() == "ready");
    assert_eq!(ui.get_gallery_image().size().width, 6);
    assert_eq!(ui.get_gallery_image_aspect(), 2.0);
    let generation = state.borrow().gallery_generation;
    ui.invoke_media_selected(99);
    ui.invoke_media_selected(-1);
    assert_eq!(state.borrow().gallery_generation, generation);
    assert_eq!(state.borrow().gallery_index, 0);
    assert_eq!(ui.get_gallery_caption(), "Снимок");
    assert_eq!(ui.get_gallery_image_state(), "ready");

    ui.set_video_playing(true);
    ui.invoke_gallery_step(-1);
    assert_eq!(ui.get_gallery_caption(), "Недоступный трейлер");
    assert!(!ui.get_video_playing());
    assert!(ui.get_video_failed());
    assert_eq!(ui.get_video_time_label(), "Ошибка видео");
    assert!(ui.get_toast().contains("Видео доступно только"));

    ui.set_video_quality(900);
    ui.invoke_gallery_step(-1);
    assert_eq!(ui.get_gallery_caption(), "Трейлер");
    assert!(!ui.get_video_failed());
    assert_eq!(ui.get_video_quality(), 720);
    assert_eq!(ui.get_video_qualities().row_count(), 3);
    assert_eq!(ui.get_video_qualities().row_data(0), Some(480));
    ui.invoke_video_quality_selected(1080);
    assert_eq!(ui.get_video_quality(), 720);
    assert!(ui.get_toast().contains("Видеоплеер недоступен"));
    wait_for(ui, |ui| ui.get_video_failed());
    assert_eq!(ui.get_video_time_label(), "Ошибка видео");

    ui.invoke_media_selected(1);
    wait_for(ui, |ui| ui.get_gallery_image_state() == "error");
    assert!(ui.get_toast().contains("Не удалось загрузить изображение"));
    image::RgbImage::from_pixel(4, 2, image::Rgb([21, 146, 77]))
        .save(state.borrow().art_root.join("damaged.png"))
        .unwrap();
    ui.invoke_media_retry();
    wait_for(ui, |ui| ui.get_gallery_image_state() == "ready");
    assert_eq!(ui.get_gallery_image().size().width, 4);

    // A queued decode from the previous slide may finish after the panel closes.
    ui.invoke_media_selected(0);
    ui.invoke_panel_closed();
    assert!(ui.get_panel_kind().is_empty());
    assert_eq!(ui.get_gallery_image().size().width, 0);
}

fn hero_switch(ui: &AppWindow, state: &State) {
    assert_eq!(ui.get_hero().size().width, 6);
    ui.invoke_search_edited("".into());

    // A catalog refresh rebinds the selection to a fresh index for the same game.
    state.borrow_mut().selected = usize::MAX;
    select_game(ui, state, 1);
    assert_eq!(ui.get_hero().size().width, 6);

    // Exercise the actual bundled hero and a reordered poster in the first frame.
    let mut app = state.borrow_mut();
    app.catalog[0].id = "peak".into();
    app.catalog[0].media = vec![Media {
        kind: "image".into(),
        path: "art/plain.png".into(),
        optimized_index: Some(2),
        ..Default::default()
    }];
    drop(app);
    let prior_generation = state.borrow().generation;
    post_hero(
        ui.as_weak(),
        prior_generation,
        SharedPixelBuffer::new(13, 3),
    );
    post_tile(
        ui.as_weak(),
        prior_generation,
        0,
        SharedPixelBuffer::new(13, 3),
    );
    let (send, receive) = std::sync::mpsc::channel();
    ui.as_weak()
        .upgrade_in_event_loop(move |ui| {
            send.send((
                ui.get_hero().size(),
                ui.get_media().row_data(0).unwrap().poster.size(),
            ))
            .unwrap();
        })
        .unwrap();
    ui.invoke_game_selected(0);
    assert_eq!(ui.get_game_title(), "Обычная игра");
    let hero = ui.get_hero().size();
    let poster = ui.get_media().row_data(0).unwrap().poster.size();
    assert!(hero.width > 13 && poster.width > 13);
    wait_for(ui, |ui| ui.get_hero().size().width == 9);
    assert_eq!(receive.try_recv().unwrap(), (hero, poster));

    // A remote-only game has no seed. Never borrow another game's image.
    ui.invoke_game_selected(1);
    assert_eq!(ui.get_game_title(), "Игра с модами");
    assert_eq!(ui.get_hero().size().width, 0);
    ui.invoke_game_selected(0);
    assert_eq!(ui.get_hero().size(), hero);
    ui.invoke_game_selected(1);
    wait_for(ui, |ui| ui.get_hero().size().width == 6);
    state.borrow_mut().catalog[0].id = "plain".into();
}

fn wait_for(ui: &AppWindow, condition: impl Fn(&AppWindow) -> bool + 'static) {
    let deadline = Instant::now() + Duration::from_secs(5);
    let reached = Rc::new(Cell::new(false));
    let ready = reached.clone();
    let weak = ui.as_weak();
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, Duration::from_millis(10), move || {
        if weak.upgrade().is_some_and(|ui| condition(&ui)) {
            ready.set(true);
        }
        if ready.get() || Instant::now() >= deadline {
            slint::quit_event_loop().unwrap();
        }
    });
    slint::run_event_loop_until_quit().unwrap();
    assert!(reached.get(), "launcher UI did not reach expected state");
}
