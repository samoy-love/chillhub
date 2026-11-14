//! Use the Unicode Shell Link interface, independent of the system ANSI locale.
use super::Link;
use std::{
    ffi::{OsStr, OsString},
    os::windows::ffi::{OsStrExt, OsStringExt},
    path::{Path, PathBuf},
};
use windows::{
    Win32::{
        System::Com::{
            CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
            CoUninitialize, IPersistFile, STGM_READ,
        },
        UI::Shell::{IShellLinkW, SLGP_RAWPATH, ShellLink},
    },
    core::{Interface, PCWSTR},
};

const MAX_WIDE: usize = 32768;

struct Apartment;
impl Drop for Apartment {
    fn drop(&mut self) {
        // This guard exists only after a successful CoInitializeEx on this thread.
        unsafe { CoUninitialize() }
    }
}

fn in_apartment<T: Send + 'static>(
    action: impl FnOnce() -> Result<T, String> + Send + 'static,
) -> Result<T, String> {
    // Keep COM interfaces on one dedicated STA; callers may already own a
    // different apartment. The worker never opens UI or runs a shortcut target.
    std::thread::Builder::new()
        .name("chillhub-shortcut".into())
        .spawn(move || {
            unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
                .ok()
                .map_err(|error| format!("Не удалось подготовить ярлыки Windows: {error}"))?;
            let _apartment = Apartment;
            action()
        })
        .map_err(|error| error.to_string())?
        .join()
        .map_err(|_| "Не удалось обработать ярлык Windows".to_owned())?
}

fn wide(value: &OsStr) -> Result<Vec<u16>, String> {
    let mut value: Vec<u16> = value.encode_wide().collect();
    if value.len() >= MAX_WIDE || value.contains(&0) {
        return Err(
            "Путь или текст ярлыка слишком длинный либо содержит недопустимый символ".into(),
        );
    }
    value.push(0);
    Ok(value)
}

fn text(buffer: &[u16]) -> Result<OsString, String> {
    let length = buffer
        .iter()
        .position(|&value| value == 0)
        .ok_or("Windows вернула слишком длинное значение ярлыка")?;
    Ok(OsString::from_wide(&buffer[..length]))
}

fn shell_link() -> Result<IShellLinkW, String> {
    unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) }
        .map_err(|error| format!("Не удалось открыть ярлык Windows: {error}"))
}

fn load(path: &Path) -> Result<IShellLinkW, String> {
    let link = shell_link()?;
    let persist: IPersistFile = link.cast().map_err(|error| error.to_string())?;
    let path = wide(path.as_os_str())?;
    unsafe { persist.Load(PCWSTR(path.as_ptr()), STGM_READ) }
        .map_err(|error| format!("Не удалось прочитать ярлык: {error}"))?;
    Ok(link)
}

pub(super) fn read(paths: &[PathBuf]) -> Result<Vec<Link>, String> {
    let paths = paths.to_vec();
    in_apartment(move || {
        let mut links = Vec::new();
        for path in paths {
            let read = || -> Result<Link, String> {
                let link = load(&path)?;
                let mut target = vec![0; MAX_WIDE];
                let mut arguments = vec![0; MAX_WIDE];
                // RAWPATH reads stored data; Resolve would allow UI/network
                // searches and could change which file a user-owned link means.
                unsafe {
                    link.GetPath(&mut target, std::ptr::null_mut(), SLGP_RAWPATH.0 as u32)
                        .map_err(|error| error.to_string())?;
                    link.GetArguments(&mut arguments)
                        .map_err(|error| error.to_string())?;
                }
                Ok(Link {
                    path: path.clone(),
                    target: text(&target)?.into(),
                    arguments: text(&arguments)?
                        .into_string()
                        .map_err(|_| "Некорректный текст параметров ярлыка")?,
                })
            };
            // Unreadable/foreign links are never changed or treated as ours.
            if let Ok(link) = read() {
                links.push(link);
            }
        }
        Ok(links)
    })
}

pub(super) fn write(
    path: &Path,
    launcher: &Path,
    arguments: &str,
    working: &Path,
    title: &str,
    icon: &Path,
) -> Result<(), String> {
    let path = wide(path.as_os_str())?;
    let launcher = wide(launcher.as_os_str())?;
    let arguments = wide(OsStr::new(arguments))?;
    let working = wide(working.as_os_str())?;
    let title = wide(OsStr::new(title))?;
    let icon = wide(icon.as_os_str())?;
    in_apartment(move || {
        let link = shell_link()?;
        unsafe {
            link.SetPath(PCWSTR(launcher.as_ptr()))
                .and_then(|()| link.SetArguments(PCWSTR(arguments.as_ptr())))
                .and_then(|()| link.SetWorkingDirectory(PCWSTR(working.as_ptr())))
                .and_then(|()| link.SetDescription(PCWSTR(title.as_ptr())))
                .and_then(|()| link.SetIconLocation(PCWSTR(icon.as_ptr()), 0))
                .map_err(|error| format!("Не удалось заполнить ярлык: {error}"))?;
            let persist: IPersistFile = link.cast().map_err(|error| error.to_string())?;
            persist
                .Save(PCWSTR(path.as_ptr()), true)
                .map_err(|error| format!("Не удалось сохранить ярлык: {error}"))?;
        }
        Ok(())
    })
}

#[cfg(test)]
pub(super) fn details(path: &Path) -> Result<(PathBuf, PathBuf, i32), String> {
    let path = path.to_owned();
    in_apartment(move || {
        let link = load(&path)?;
        let mut working = vec![0; MAX_WIDE];
        let mut icon = vec![0; MAX_WIDE];
        let mut index = 0;
        unsafe {
            link.GetWorkingDirectory(&mut working)
                .map_err(|error| error.to_string())?;
            link.GetIconLocation(&mut icon, &mut index)
                .map_err(|error| error.to_string())?;
        }
        Ok((text(&working)?.into(), text(&icon)?.into(), index))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unicode_buffers_reject_nul_and_truncation() {
        assert!(wide(OsStr::new("a\0b")).is_err());
        assert!(wide(OsStr::new(&"x".repeat(MAX_WIDE))).is_err());
        assert!(text(&[1, 2]).is_err());
        assert_eq!(
            text(&wide(OsStr::new("游戏 🎮")).unwrap()).unwrap(),
            "游戏 🎮"
        );
    }
}
