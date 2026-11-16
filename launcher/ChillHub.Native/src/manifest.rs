//! Wire manifest and Windows-safe content paths, shared by download and update code.
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    fs::{self, File, OpenOptions},
    io,
    path::{Component, Path, PathBuf},
};

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub build_id: String,
    #[serde(default)]
    pub game_id: String,
    #[serde(default)]
    pub created_at: String,
    #[serde(default)]
    pub block_size: u64,
    pub files: Vec<ManifestFile>,
    #[serde(default, deserialize_with = "empty_directories")]
    pub empty_dirs: Vec<String>,
}

// Published C# manifests use a trailing separator for empty directories. This is
// a directory-only wire convention: file paths and internal separators stay strict.
fn empty_directories<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> Result<Vec<String>, D::Error> {
    Vec::<String>::deserialize(deserializer).map(|dirs| {
        dirs.into_iter()
            .map(|dir| dir.trim_end_matches(['/', '\\']).to_owned())
            .collect()
    })
}
impl Manifest {
    pub fn normalize_empty_dirs(&mut self) {
        for dir in &mut self.empty_dirs {
            *dir = dir.trim_end_matches(['/', '\\']).to_owned();
        }
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestFile {
    pub path: String,
    pub size: u64,
    #[serde(default)]
    pub blake3: String,
    #[serde(default)]
    pub sha256: Option<String>,
    #[serde(default)]
    pub executable: bool,
    #[serde(default)]
    pub blocks: Option<String>,
}

pub fn validate_relative(path: &str) -> Result<(), String> {
    if path.is_empty() || path.len() > 1024 || path != path.trim() {
        return Err(format!("Недопустимый путь в списке файлов: {path:?}"));
    }
    if path
        .chars()
        .any(|c| c.is_control() || "\\:*?\"<>|".contains(c))
    {
        return Err(format!("Недопустимые символы в пути: {path:?}"));
    }
    for segment in path.split('/') {
        if segment.is_empty()
            || segment == "."
            || segment == ".."
            || segment.starts_with(' ')
            || segment.ends_with([' ', '.'])
        {
            return Err(format!("Небезопасный путь: {path:?}"));
        }
        let stem = segment.split('.').next().unwrap_or("").to_uppercase();
        let numbered_device = ["COM", "LPT"].iter().any(|prefix| {
            stem.strip_prefix(prefix).is_some_and(|n| {
                matches!(
                    n,
                    "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
                )
            })
        });
        if matches!(
            stem.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
        ) || numbered_device
        {
            return Err(format!("Зарезервированное имя Windows: {path:?}"));
        }
    }
    Ok(())
}

pub fn validate_manifest(manifest: &Manifest) -> Result<(), String> {
    let mut files = HashSet::new();
    let mut dirs = HashSet::new();
    let mut spellings = HashMap::new();
    for file in &manifest.files {
        validate_relative(&file.path)?;
        let key = file.path.to_lowercase();
        if !files.insert(key.clone()) {
            return Err(format!("Файл повторяется: {}", file.path));
        }
        let mut has_hash = false;
        for hash in [Some(file.blake3.as_str()), file.sha256.as_deref()]
            .into_iter()
            .flatten()
            .filter(|h| !h.is_empty())
        {
            if hash.len() != 64 || !hash.bytes().all(|b| b.is_ascii_hexdigit()) {
                return Err(format!("Повреждён хеш файла: {}", file.path));
            }
            has_hash = true;
        }
        if !has_hash {
            return Err(format!("Нет хеша файла: {}", file.path));
        }
        check_spelling(&file.path, &mut spellings)?;
    }
    for dir in &manifest.empty_dirs {
        let dir = dir.trim_end_matches(['/', '\\']);
        validate_relative(dir)?;
        if !dirs.insert(dir.to_lowercase()) {
            return Err(format!("Папка повторяется: {dir}"));
        }
        check_spelling(dir, &mut spellings)?;
    }
    for path in files.iter().chain(dirs.iter()) {
        if dirs.contains(path) && files.contains(path) {
            return Err(format!("Файл совпадает с папкой: {path}"));
        }
        let mut ancestor = path.as_str();
        while let Some((parent, _)) = ancestor.rsplit_once('/') {
            if files.contains(parent) {
                return Err(format!("Файл используется как папка: {parent}"));
            }
            ancestor = parent;
        }
    }
    // Temporary siblings must never overlap a legitimate manifest file.
    for path in &files {
        if files.contains(&format!("{path}.part")) || dirs.contains(&format!("{path}.part")) {
            return Err(format!("Файл пересекается с временным файлом: {path}"));
        }
    }
    Ok(())
}

fn check_spelling(path: &str, names: &mut HashMap<String, String>) -> Result<(), String> {
    let mut prefix = String::new();
    for part in path.split('/') {
        if !prefix.is_empty() {
            prefix.push('/');
        }
        prefix.push_str(part);
        if let Some(old) = names.insert(prefix.to_lowercase(), prefix.clone())
            && old != prefix
        {
            return Err(format!("Разный регистр одного пути: {old} / {prefix}"));
        }
    }
    Ok(())
}

pub fn safe_join(root: &Path, relative: &str) -> Result<PathBuf, String> {
    validate_relative(relative)?;
    check_no_links(root).map_err(|e| e.to_string())?;
    let mut result = root.to_path_buf();
    for part in relative.split('/') {
        result.push(part);
        reject_link(&result).map_err(|e| e.to_string())?;
    }
    Ok(result)
}

pub fn is_reparse(meta: &fs::Metadata) -> bool {
    if meta.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return true;
        }
    }
    false
}

fn reject_link(path: &Path) -> io::Result<()> {
    match fs::symlink_metadata(path) {
        Ok(meta) if is_reparse(&meta) => Err(io::Error::other(format!(
            "Ссылка или точка повторной обработки: {}",
            path.display()
        ))),
        Ok(_) => Ok(()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(e),
    }
}

pub fn check_no_links(path: &Path) -> io::Result<()> {
    if !path.is_absolute()
        || path
            .components()
            .any(|c| matches!(c, Component::ParentDir | Component::CurDir))
    {
        return Err(io::Error::other(
            "Нужен абсолютный путь без переходов к родительским папкам",
        ));
    }
    for ancestor in path.ancestors() {
        reject_link(ancestor)?;
    }
    Ok(())
}

/// Windows handles deny renaming ancestors for the lifetime of an operation.
/// Each newly-created directory is opened without following reparse points.
pub struct DirectoryGuard {
    _handles: Vec<File>,
}

// Another process may create the same directory after the existence check.
// Other errors must remain visible; the opened handle is validated below.
fn create_directory_if_absent(path: &Path) -> io::Result<()> {
    match fs::create_dir(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => Ok(()),
        Err(error) => Err(error),
    }
}
impl DirectoryGuard {
    pub fn acquire(path: &Path, create: bool) -> io::Result<Self> {
        check_no_links(path)?;
        let ancestors: Vec<_> = path.ancestors().collect();
        let mut handles = Vec::new();
        for dir in ancestors.into_iter().rev() {
            if create && !dir.exists() {
                create_directory_if_absent(dir)?;
            }
            if !dir.exists() {
                continue;
            }
            let mut options = OpenOptions::new();
            options.read(true);
            #[cfg(windows)]
            {
                use std::os::windows::fs::OpenOptionsExt;
                options
                    // Attribute-only handles do not participate in Windows
                    // read/write/delete sharing checks. Request directory read
                    // access as well so omitting FILE_SHARE_DELETE pins identity.
                    .access_mode(0x81)
                    .share_mode(0x1 | 0x2)
                    .custom_flags(0x00200000 | 0x02000000);
            }
            let file = options.open(dir)?;
            let meta = file.metadata()?;
            if !meta.is_dir() || is_reparse(&meta) {
                return Err(io::Error::other(format!(
                    "Небезопасная папка: {}",
                    dir.display()
                )));
            }
            handles.push(file);
        }
        Ok(Self { _handles: handles })
    }
}

fn validate_regular_metadata(meta: &fs::Metadata) -> io::Result<()> {
    if !meta.is_file() || is_reparse(meta) {
        return Err(io::Error::other("Ожидался обычный файл"));
    }
    Ok(())
}

pub fn open_regular(path: &Path, write: bool, create: bool, truncate: bool) -> io::Result<File> {
    reject_link(path)?;
    let mut options = OpenOptions::new();
    // Inspect the opened handle before truncation: a planted hard link must not
    // turn a temporary download into a write outside the installation.
    options.read(true).write(write).create(create);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.custom_flags(0x00200000).share_mode(0x1);
    }
    let file = options.open(path)?;
    let meta = file.metadata()?;
    validate_regular_metadata(&meta)?;
    if write {
        #[cfg(windows)]
        {
            use std::os::windows::io::AsRawHandle;
            use windows_sys::Win32::Storage::FileSystem::{
                BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle,
            };
            let mut information: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
            if unsafe { GetFileInformationByHandle(file.as_raw_handle() as _, &mut information) }
                == 0
            {
                return Err(io::Error::last_os_error());
            }
            if information.nNumberOfLinks > 1 {
                return Err(io::Error::other("Запись в жёсткую ссылку запрещена"));
            }
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            if meta.nlink() > 1 {
                return Err(io::Error::other("Запись в жёсткую ссылку запрещена"));
            }
        }
    }
    if truncate {
        file.set_len(0)?;
    }
    Ok(file)
}

pub fn is_service_path(path: &str) -> bool {
    matches!(
        path.to_lowercase().as_str(),
        ".version"
            | ".mods.version"
            | ".mods.revision"
            | ".mods.manifest.json"
            | ".updating"
            | ".chillhub.lock"
            | ".integrity.json"
    ) || path.eq_ignore_ascii_case("freetp/.hash")
        || path.eq_ignore_ascii_case(".chillhub-reboot")
        || path.to_ascii_lowercase().starts_with(".chillhub-reboot/")
}

#[cfg(test)]
#[path = "manifest_tests.rs"]
mod tests;
