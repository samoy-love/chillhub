//! Bounded, read-only support bundle. Never walk user links or collect arbitrary
//! configuration fields, saves, screenshots, browser profiles or credentials.
use crate::{config::ConfigStore, paths::AppPaths};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{Read, Seek, SeekFrom},
    path::Path,
    time::{Duration, Instant},
};

const MAX_BUNDLE: usize = 1024 * 1024;
const MAX_LOGS: usize = 512 * 1024;
const MAX_HASH_BYTES: u64 = 10 * 1024 * 1024;

struct Bundle {
    text: String,
    logs_left: usize,
    deadline: Instant,
}
impl Bundle {
    fn new() -> Self {
        Self {
            text: String::new(),
            logs_left: MAX_LOGS,
            deadline: Instant::now() + Duration::from_secs(3),
        }
    }
    fn available(&self) -> bool {
        self.text.len() < MAX_BUNDLE && Instant::now() < self.deadline
    }
    fn append(&mut self, text: &str) {
        let remaining = MAX_BUNDLE.saturating_sub(self.text.len());
        let mut end = text.len().min(remaining);
        while !text.is_char_boundary(end) {
            end -= 1;
        }
        self.text.push_str(&text[..end]);
    }
    fn section(&mut self, title: &str) {
        self.append(&format!("\n## {title}\n"));
    }
    fn tail(&mut self, root: &Path, relative: &str, limit: usize) {
        if !self.available() || self.logs_left == 0 {
            return;
        }
        let Ok(path) = crate::manifest::safe_join(root, relative) else {
            return;
        };
        let Ok(mut file) = crate::manifest::open_regular(&path, false, false, false) else {
            return;
        };
        let Ok(meta) = file.metadata() else {
            return;
        };
        let count = (meta.len().min(limit.min(self.logs_left) as u64)) as usize;
        if file
            .seek(SeekFrom::Start(meta.len().saturating_sub(count as u64)))
            .is_err()
        {
            return;
        }
        let mut bytes = Vec::new();
        if file.take(count as u64).read_to_end(&mut bytes).is_ok() {
            self.logs_left = self.logs_left.saturating_sub(bytes.len());
            self.append(&format!("\n### {relative} ({} bytes)\n", meta.len()));
            if meta.len() > bytes.len() as u64 {
                self.append("[earlier content omitted]\n");
            }
            self.append(&String::from_utf8_lossy(&bytes));
            self.append("\n");
        }
    }
    fn tree(&mut self, root: &Path, depth: usize, limit: usize, folders_only: bool) {
        if crate::manifest::check_no_links(root).is_err() {
            return;
        }
        for entry in walkdir::WalkDir::new(root)
            .follow_links(false)
            .min_depth(1)
            .max_depth(depth)
            .into_iter()
            .take(limit)
        {
            if !self.available() {
                break;
            }
            let Ok(entry) = entry else {
                continue;
            };
            if entry.file_type().is_symlink() || (folders_only && !entry.file_type().is_dir()) {
                continue;
            }
            let Ok(relative) = entry.path().strip_prefix(root) else {
                continue;
            };
            let size = entry.metadata().map(|m| m.len()).unwrap_or(0);
            self.append(&format!(
                "{}{} [{} bytes]\n",
                relative.display(),
                if entry.file_type().is_dir() { "/" } else { "" },
                size
            ));
        }
    }
    fn helper_logs(&mut self, directory: &Path) {
        if crate::manifest::check_no_links(directory).is_err() {
            return;
        }
        let Ok(entries) = fs::read_dir(directory) else {
            return;
        };
        let mut candidates = Vec::new();
        for entry in entries.take(256).filter_map(Result::ok) {
            if !self.available() {
                break;
            }
            let name = entry.file_name().to_string_lossy().into_owned();
            let Some(kind) = ["updater", "security"].into_iter().find(|prefix| {
                name.strip_prefix(&format!("{prefix}-"))
                    .and_then(|s| s.strip_suffix(".jsonl"))
                    .is_some_and(|s| uuid::Uuid::parse_str(s).is_ok())
            }) else {
                continue;
            };
            let Ok(meta) = fs::symlink_metadata(entry.path()) else {
                continue;
            };
            if meta.is_file() && !crate::manifest::is_reparse(&meta) {
                candidates.push((kind, meta.modified().ok(), name));
            }
        }
        candidates.sort_by_key(|entry| std::cmp::Reverse(entry.1));
        for kind in ["updater", "security"] {
            for (_, _, name) in candidates.iter().filter(|(k, _, _)| *k == kind).take(2) {
                self.tail(directory, name, 24 * 1024);
            }
        }
    }
    fn launcher_files(&mut self, install: &Path) {
        if crate::manifest::check_no_links(install).is_err() {
            return;
        }
        let mut hash_left = MAX_HASH_BYTES;
        for entry in walkdir::WalkDir::new(install)
            .follow_links(false)
            .max_depth(3)
            .into_iter()
            .take(200)
        {
            if !self.available() {
                break;
            }
            let Ok(entry) = entry else {
                continue;
            };
            if !entry.file_type().is_file() {
                continue;
            }
            let Ok(relative) = entry.path().strip_prefix(install) else {
                continue;
            };
            let Ok(mut file) = crate::manifest::open_regular(entry.path(), false, false, false)
            else {
                continue;
            };
            let size = file.metadata().map(|m| m.len()).unwrap_or(0);
            let mut digest = String::new();
            if size <= 5 * 1024 * 1024 && size <= hash_left {
                let mut hasher = Sha256::new();
                let mut buffer = [0_u8; 32 * 1024];
                let mut complete = true;
                let mut remaining = size;
                while remaining > 0 {
                    if !self.available() {
                        complete = false;
                        break;
                    }
                    let wanted = remaining.min(buffer.len() as u64) as usize;
                    match file.read(&mut buffer[..wanted]) {
                        Ok(0) | Err(_) => {
                            complete = false;
                            break;
                        }
                        Ok(n) => {
                            hasher.update(&buffer[..n]);
                            remaining -= n as u64;
                        }
                    }
                }
                hash_left = hash_left.saturating_sub(size);
                if complete {
                    digest = format!(" sha256={}", hex::encode(hasher.finalize()));
                }
            }
            self.append(&format!("{} [{size} bytes]{digest}\n", relative.display()));
        }
    }
}

pub fn collect(data: &Path) -> String {
    let install = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(Path::to_owned));
    collect_at(data, install.as_deref())
}

/// Capture the error-time log before a panic can terminate the process. The
/// expensive filesystem inventory belongs to the background report sender.
pub(crate) fn recent_log(data: &Path) -> String {
    let mut bundle = Bundle::new();
    bundle.append(&format!("Chill Hub {}\n", env!("CARGO_PKG_VERSION")));
    bundle.logs_left = 48 * 1024;
    for directory in crate::diagnostics::log_directories(data) {
        bundle.tail(&directory, "launcher.log", 48 * 1024);
    }
    crate::diagnostics::redact(&bundle.text)
}

fn collect_at(data: &Path, install: Option<&Path>) -> String {
    let mut bundle = Bundle::new();
    bundle.append(&format!(
        "Chill Hub {}\nRuntime: Rust + Slint\nOS: {}\nArch: {}\nGenerated: {}\n",
        env!("CARGO_PKG_VERSION"),
        crate::diagnostics::os_version(),
        std::env::consts::ARCH,
        chrono::Utc::now().to_rfc3339()
    ));
    let paths = AppPaths {
        data_dir: data.into(),
        legacy_dir: None,
        games_dir: crate::paths::default_games_dir(),
    };
    bundle.section("Settings");
    let config = if paths.config_path().is_file() {
        ConfigStore::new(paths).load_readonly()
    } else {
        Err("Настройки ещё не созданы".into())
    };
    match config {
        Ok(config) => {
            // Explicit allowlist: future config keys may contain private credentials.
            let endpoint = url::Url::parse(&config.api_base_url)
                .ok()
                .map(|u| u.origin().ascii_serialization());
            bundle.append(&serde_json::json!({"gamesPath":config.games_path,"downloadThreads":config.download_threads,"speedLimitMbps":config.speed_limit_mbps,"apiOrigin":endpoint,"minimizeToTray":config.minimize_to_tray,"window":[config.window_width,config.window_height],"maximized":config.window_maximized}).to_string());
            let games = Path::new(&config.games_path);
            bundle.section("Games (depth 2, up to 200 entries)");
            bundle.tree(games, 2, 200, true);
            bundle.section("Installed versions and mods");
            if crate::manifest::check_no_links(games).is_ok()
                && let Ok(entries) = fs::read_dir(games)
            {
                let mut mods_count = 0;
                for entry in entries.take(200).filter_map(Result::ok) {
                    if !bundle.available() {
                        break;
                    }
                    let root = entry.path();
                    if !entry.file_type().is_ok_and(|t| t.is_dir())
                        || crate::manifest::check_no_links(&root).is_err()
                    {
                        continue;
                    }
                    bundle.append(&format!("\n### {}\n", entry.file_name().to_string_lossy()));
                    for marker in [".version", ".mods.version", ".mods.revision"] {
                        bundle.tail(&root, marker, 8192);
                    }
                    if let Ok(path) = crate::manifest::safe_join(&root, ".mods.manifest.json")
                        && let Ok(meta) = fs::metadata(path)
                    {
                        bundle.append(&format!(".mods.manifest.json [{} bytes]\n", meta.len()));
                    }
                    if mods_count < 6 && root.join("BepInEx").is_dir() {
                        mods_count += 1;
                        bundle.tail(&root, "doorstop_config.ini", 8192);
                        bundle.tree(&root.join("BepInEx"), 2, 120, false);
                        bundle.tail(&root, "BepInEx/LogOutput.log", 48 * 1024);
                    }
                }
            }
        }
        Err(error) => bundle.append(&format!("{error}\n")),
    }
    bundle.section("Launcher and updater logs");
    for directory in crate::diagnostics::log_directories(data) {
        bundle.tail(&directory, "launcher.log", 96 * 1024);
        bundle.helper_logs(&directory);
        for name in [
            "launcher.previous.log",
            "updater.log",
            "security.log",
            "client.log",
            "client.1.log",
            "boot.log",
        ] {
            bundle.tail(&directory, name, 96 * 1024);
        }
    }
    for relative in ["update-status.json", "startup-error.log"] {
        bundle.tail(data, relative, 96 * 1024);
    }
    if let Some(install) = install {
        bundle.section("Launcher files (bounded SHA-256)");
        bundle.launcher_files(install);
    }
    if !bundle.available() {
        bundle.append("\n[diagnostic budget reached]\n");
    }
    crate::diagnostics::redact(&bundle.text)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn includes_versions_mod_logs_and_only_safe_settings() {
        let tmp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(tmp.path());
        let store = ConfigStore::new(paths.clone());
        let mut config = store.load().unwrap();
        config
            .extra
            .insert("Password".into(), "never-collect-this".into());
        config.api_base_url = "https://example.com/?secret=private-query".into();
        store.save(&config).unwrap();
        let game = paths.games_dir.join("peak");
        fs::create_dir_all(game.join("BepInEx")).unwrap();
        fs::write(game.join(".version"), "1.2.3").unwrap();
        fs::write(game.join("BepInEx/LogOutput.log"), "mod diagnostic line").unwrap();
        let report = collect_at(&paths.data_dir, None);
        assert!(report.contains("1.2.3") && report.contains("mod diagnostic line"));
        assert!(!report.contains("never-collect-this") && !report.contains("private-query"));
    }
    #[test]
    fn huge_logs_are_tailed_without_exceeding_bundle_budget() {
        let tmp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(tmp.path());
        ConfigStore::new(paths.clone()).load().unwrap();
        fs::create_dir_all(paths.data_dir.join("logs")).unwrap();
        let mut log = "x".repeat(3 * 1024 * 1024);
        log.push_str("RECENT-END");
        fs::write(paths.data_dir.join("logs/launcher.log"), log).unwrap();
        let report = collect_at(&paths.data_dir, None);
        assert!(report.len() <= MAX_BUNDLE && report.contains("RECENT-END"));
        assert!(report.contains("earlier content omitted"));
    }
    #[test]
    fn collecting_missing_or_corrupt_settings_never_writes_them() {
        let tmp = tempfile::tempdir().unwrap();
        let path = tmp.path().join("config.json");
        let _ = collect_at(tmp.path(), None);
        assert!(!path.exists());
        fs::write(&path, b"broken").unwrap();
        let report = collect_at(tmp.path(), None);
        assert!(report.contains("повреждён"));
        assert_eq!(fs::read(path).unwrap(), b"broken");
    }
    #[test]
    fn includes_current_helper_logs_and_ignores_unrecognized_names() {
        let tmp = tempfile::tempdir().unwrap();
        fs::create_dir(tmp.path().join("logs")).unwrap();
        for (kind, message) in [
            ("updater", "update-failure-details"),
            ("security", "security-check-details"),
        ] {
            fs::write(tmp.path().join(format!("logs/{kind}-{}.jsonl", uuid::Uuid::new_v4())),
                serde_json::json!({"at":"2026-11-10T00:00:00Z","event":"failure","message":message}).to_string()).unwrap();
        }
        fs::write(
            tmp.path().join("logs/security-private-notes.jsonl"),
            "unrelated-private-text",
        )
        .unwrap();
        let report = collect_at(tmp.path(), None);
        assert!(
            report.contains("update-failure-details") && report.contains("security-check-details")
        );
        assert!(!report.contains("unrelated-private-text"));
        assert!(report.len() <= MAX_BUNDLE);
    }
}
