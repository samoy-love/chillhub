//! Cleanup consent is tied to one file and verified while holding its deletion handle.
use super::{Result, TransferControl, TransferError};
use crate::manifest::{self, DirectoryGuard, open_regular};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    fs::{self, File},
    io::{self, Read},
    path::Path,
};

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct CleanupFile {
    pub path: String,
    identity: FileIdentity,
    digest: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
struct FileIdentity {
    volume: u64,
    index: [u8; 16],
    generation: u64,
}

pub(super) fn is_approved(planned: &[CleanupFile], approved: &[CleanupFile]) -> bool {
    let approved: HashSet<_> = approved.iter().collect();
    planned.iter().all(|file| approved.contains(file))
}

pub(super) fn snapshot(
    path: &Path,
    relative: &str,
    control: &TransferControl,
) -> Result<Option<CleanupFile>> {
    let _directory = DirectoryGuard::acquire(path.parent().unwrap(), false)?;
    let file = match open_regular(path, false, false, false) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error.into()),
    };
    capture(&file, relative, control).map(Some)
}

pub(super) enum CleanupResult {
    Removed,
    Missing,
    Changed(CleanupFile),
}

pub(super) fn remove_approved(
    path: &Path,
    approved: &CleanupFile,
    control: &TransferControl,
) -> Result<CleanupResult> {
    let _directory = DirectoryGuard::acquire(path.parent().unwrap(), false)?;
    let file = match open_for_delete(path) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(CleanupResult::Missing),
        Err(error) => return Err(error.into()),
    };
    let current = capture(&file, &approved.path, control)?;
    if current != *approved {
        return Ok(CleanupResult::Changed(current));
    }
    control.checkpoint()?;
    delete_open_file(path, file)?;
    Ok(CleanupResult::Removed)
}

fn capture(file: &File, relative: &str, control: &TransferControl) -> Result<CleanupFile> {
    let metadata = file.metadata()?;
    if !metadata.is_file() || manifest::is_reparse(&metadata) {
        return Err(TransferError::UnsafePath(relative.into()));
    }
    let identity = file_identity(file)?;
    let mut reader = file;
    let mut digest = blake3::Hasher::new();
    let mut buffer = [0; 64 * 1024];
    loop {
        control.checkpoint()?;
        let bytes = reader.read(&mut buffer)?;
        if bytes == 0 {
            break;
        }
        digest.update(&buffer[..bytes]);
    }
    if identity != file_identity(file)? {
        return Err(
            io::Error::other(format!("Файл изменился во время проверки: {relative}")).into(),
        );
    }
    Ok(CleanupFile {
        path: relative.into(),
        identity,
        digest: digest.finalize().to_hex().to_string(),
    })
}

#[cfg(windows)]
fn file_identity(file: &File) -> io::Result<FileIdentity> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        BY_HANDLE_FILE_INFORMATION, FILE_ID_INFO, FileIdInfo, GetFileInformationByHandle,
        GetFileInformationByHandleEx,
    };
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    let handle = file.as_raw_handle().cast();
    if unsafe { GetFileInformationByHandle(handle, &mut info) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let generation = (u64::from(info.ftCreationTime.dwHighDateTime) << 32)
        | u64::from(info.ftCreationTime.dwLowDateTime);
    let mut id = FILE_ID_INFO::default();
    if unsafe {
        GetFileInformationByHandleEx(
            handle,
            FileIdInfo,
            (&raw mut id).cast(),
            size_of::<FILE_ID_INFO>() as u32,
        )
    } != 0
    {
        return Ok(FileIdentity {
            volume: id.VolumeSerialNumber,
            index: id.FileId.Identifier,
            generation,
        });
    }
    // FAT and some older filesystem drivers only provide the legacy file index.
    let index = (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow);
    Ok(FileIdentity {
        volume: u64::from(info.dwVolumeSerialNumber),
        index: u128::from(index).to_le_bytes(),
        generation,
    })
}

#[cfg(unix)]
fn file_identity(file: &File) -> io::Result<FileIdentity> {
    use std::os::unix::fs::MetadataExt;
    let metadata = file.metadata()?;
    Ok(FileIdentity {
        volume: metadata.dev(),
        index: u128::from(metadata.ino()).to_le_bytes(),
        generation: (metadata.ctime() as u64)
            .wrapping_mul(1_000_000_000)
            .wrapping_add(metadata.ctime_nsec() as u64),
    })
}

#[cfg(windows)]
fn open_for_delete(path: &Path) -> io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::{
        Foundation::GENERIC_READ,
        Storage::FileSystem::{DELETE, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ},
    };
    // Do not allow a writer or a rename to race with the final hash check.
    fs::OpenOptions::new()
        .access_mode(GENERIC_READ | DELETE)
        .share_mode(FILE_SHARE_READ)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
}

#[cfg(unix)]
fn open_for_delete(path: &Path) -> io::Result<File> {
    open_regular(path, false, false, false)
}

#[cfg(windows)]
fn delete_open_file(_: &Path, file: File) -> io::Result<()> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_DISPOSITION_FLAG_DELETE, FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        FILE_DISPOSITION_INFO, FILE_DISPOSITION_INFO_EX, FileDispositionInfo,
        FileDispositionInfoEx, SetFileInformationByHandle,
    };
    let handle = file.as_raw_handle().cast();
    let info = FILE_DISPOSITION_INFO_EX {
        Flags: FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
    };
    if unsafe {
        SetFileInformationByHandle(
            handle,
            FileDispositionInfoEx,
            (&raw const info).cast(),
            size_of::<FILE_DISPOSITION_INFO_EX>() as u32,
        )
    } != 0
    {
        return Ok(());
    }
    let error = io::Error::last_os_error();
    if !matches!(error.raw_os_error(), Some(1 | 50 | 87)) {
        return Err(error);
    }
    let info = FILE_DISPOSITION_INFO { DeleteFile: true };
    if unsafe {
        SetFileInformationByHandle(
            handle,
            FileDispositionInfo,
            (&raw const info).cast(),
            size_of::<FILE_DISPOSITION_INFO>() as u32,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

#[cfg(unix)]
fn delete_open_file(path: &Path, file: File) -> io::Result<()> {
    // POSIX unlink accepts a path. Recheck its identity immediately before
    // unlinking rather than acting on an already-replaced directory entry.
    let current = open_regular(path, false, false, false)?;
    if file_identity(&current)? != file_identity(&file)? {
        return Err(io::Error::other("Файл изменился во время проверки"));
    }
    fs::remove_file(path)
}

#[cfg(test)]
#[path = "cleanup_tests.rs"]
mod tests;
