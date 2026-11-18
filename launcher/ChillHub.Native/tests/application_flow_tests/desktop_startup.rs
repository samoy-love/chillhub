use super::{AppConfig, AppPaths, ConfigStore, Duration, Instant, fixture_game, fs, thread};
use std::{
    path::Path,
    process::{Child, Command, ExitStatus},
};
use windows_sys::Win32::{
    Foundation::{HWND, LPARAM},
    UI::WindowsAndMessaging::{
        EnumWindows, GetWindowTextW, GetWindowThreadProcessId, IsWindowVisible, PostMessageW,
        WM_CLOSE,
    },
};

struct Preview(Child);

impl Preview {
    fn open(exe: &Path, profile: &Path) -> Self {
        Self(
            Command::new(exe)
                .arg("--preview-directory")
                .arg(profile)
                .spawn()
                .unwrap(),
        )
    }

    fn wait_for_exit(&mut self) -> ExitStatus {
        let deadline = Instant::now() + Duration::from_secs(25);
        loop {
            if let Some(status) = self.0.try_wait().unwrap() {
                return status;
            }
            assert!(Instant::now() < deadline, "preview did not exit");
            thread::sleep(Duration::from_millis(50));
        }
    }
}

impl Drop for Preview {
    fn drop(&mut self) {
        if self.0.try_wait().ok().flatten().is_none() {
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }
}

fn preview_window(pid: u32) -> Option<HWND> {
    struct Search {
        pid: u32,
        window: Option<HWND>,
    }
    unsafe extern "system" fn visit(hwnd: HWND, param: LPARAM) -> i32 {
        // EnumWindows calls this synchronously while the stack value is alive.
        let search = unsafe { &mut *(param as *mut Search) };
        let mut owner = 0;
        unsafe { GetWindowThreadProcessId(hwnd, &mut owner) };
        if owner == search.pid && unsafe { IsWindowVisible(hwnd) } != 0 {
            let mut title = [0; 96];
            let len = unsafe { GetWindowTextW(hwnd, title.as_mut_ptr(), title.len() as i32) };
            if String::from_utf16_lossy(&title[..len as usize]) == "Chill Hub · Проверочная копия"
            {
                search.window = Some(hwnd);
                return 0;
            }
        }
        1
    }
    let mut search = Search { pid, window: None };
    unsafe { EnumWindows(Some(visit), (&mut search as *mut Search) as LPARAM) };
    search.window
}

#[test]
fn second_start_activates_the_same_preview_and_the_primary_saves_on_close() {
    let temp = tempfile::tempdir().unwrap();
    let package = temp.path().join("package");
    fs::create_dir_all(package.join("Data")).unwrap();
    fs::write(package.join("Data/games.json"), "[]").unwrap();
    let exe = package.join("ChillHub.exe");
    fs::copy(env!("CARGO_BIN_EXE_ChillHub"), &exe).unwrap();
    let (server, _) = fixture_game();
    let profile = temp.path().join("profile");
    let mut paths = AppPaths::isolated(&profile);
    paths.data_dir = profile.join("state");
    let config = ConfigStore::new(paths.clone());
    config
        .save(&AppConfig {
            games_path: paths.games_dir.to_string_lossy().into_owned(),
            api_base_url: server.base.clone(),
            minimize_to_tray: false,
            last_seen_changelog_version: env!("CARGO_PKG_VERSION").into(),
            ..Default::default()
        })
        .unwrap();

    let mut primary = Preview::open(&exe, &profile);
    let deadline = Instant::now() + Duration::from_secs(35);
    let window = loop {
        assert!(
            primary.0.try_wait().unwrap().is_none(),
            "preview exited at startup"
        );
        if let Some(hwnd) = preview_window(primary.0.id())
            && server
                .requests
                .lock()
                .unwrap()
                .iter()
                .any(|(url, _)| url == "/api/games")
        {
            break hwnd;
        }
        assert!(
            Instant::now() < deadline,
            "preview did not open its window and library"
        );
        thread::sleep(Duration::from_millis(50));
    };
    let mut secondary = Preview::open(&exe, &profile);
    assert!(secondary.wait_for_exit().success());
    assert!(primary.0.try_wait().unwrap().is_none());
    assert_eq!(preview_window(primary.0.id()), Some(window));
    assert_ne!(unsafe { PostMessageW(window, WM_CLOSE, 0, 0) }, 0);
    assert!(primary.wait_for_exit().success());
    let saved = config.load_readonly().unwrap();
    assert!(!saved.minimize_to_tray);
    assert!(saved.window_width > 0.0 && saved.window_height > 0.0);
}
