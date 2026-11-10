//! Shared native folder chooser, invoked on a dedicated STA thread.
use std::path::{Path, PathBuf};
#[cfg(windows)]
pub fn choose_folder(owner: isize, initial: &Path) -> Result<Option<PathBuf>, String> {
    use windows::{
        Win32::{
            Foundation::HWND,
            System::Com::{
                CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoTaskMemFree, CoUninitialize,
            },
            UI::Shell::{
                FOS_FORCEFILESYSTEM, FOS_NOCHANGEDIR, FOS_PATHMUSTEXIST, FOS_PICKFOLDERS,
                FileOpenDialog, IFileOpenDialog, IShellItem, SHCreateItemFromParsingName,
                SIGDN_FILESYSPATH,
            },
        },
        core::HSTRING,
    };
    // Called on a dedicated worker. Every initialized apartment is uninitialized,
    // including user cancellation and COM errors; no window is created in tests.
    unsafe {
        CoInitializeEx(None, COINIT_APARTMENTTHREADED)
            .ok()
            .map_err(|e| e.to_string())?;
        struct Apartment;
        impl Drop for Apartment {
            fn drop(&mut self) {
                unsafe {
                    CoUninitialize();
                }
            }
        }
        let _apartment = Apartment;
        let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)
            .map_err(|e| e.to_string())?;
        dialog
            .SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR)
            .map_err(|e| e.to_string())?;
        dialog
            .SetTitle(&HSTRING::from("Выберите папку Chill Hub"))
            .map_err(|e| e.to_string())?;
        let initial = initial.to_path_buf();
        if initial.is_dir() {
            let folder: windows::core::Result<IShellItem> =
                SHCreateItemFromParsingName(&HSTRING::from(initial.as_os_str()), None);
            if let Ok(folder) = folder {
                let _ = dialog.SetFolder(&folder);
            }
        }
        if let Err(e) = dialog.Show(if owner == 0 {
            None
        } else {
            Some(HWND(owner as *mut _))
        }) {
            if e.code().0 as u32 == 0x800704c7 {
                return Ok(None);
            }
            return Err(format!("Не удалось открыть выбор папки: {e}"));
        }
        let name = dialog
            .GetResult()
            .and_then(|v| v.GetDisplayName(SIGDN_FILESYSPATH))
            .map_err(|e| e.to_string())?;
        let result = name
            .to_string()
            .map(PathBuf::from)
            .map_err(|e| e.to_string());
        CoTaskMemFree(Some(name.0 as *const _));
        result.map(Some)
    }
}
#[cfg(not(windows))]
pub fn choose_folder(_: isize, _: &Path) -> Result<Option<PathBuf>, String> {
    Err("Выбор папки доступен в Windows".into())
}
