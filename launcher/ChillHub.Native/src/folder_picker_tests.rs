use super::*;
use windows::core::{Error, HRESULT};

#[test]
fn cancellation_is_distinct_from_a_failed_dialog() {
    let cancel = Error::from_hresult(HRESULT(0x800704c7_u32 as i32));
    let access_denied = Error::from_hresult(HRESULT(0x80070005_u32 as i32));
    assert!(dialog_accepted(Ok(())).unwrap());
    assert!(!dialog_accepted(Err(cancel)).unwrap());
    assert!(dialog_accepted(Err(access_denied)).is_err());
}

#[test]
fn incompatible_com_apartment_fails_without_opening_a_window() {
    use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
    std::thread::spawn(|| {
        let initialized = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
        assert!(initialized.is_ok());
        let result = choose_folder(0, Path::new(""));
        unsafe { CoUninitialize() };
        assert!(result.unwrap_err().contains("0x80010106"));
    })
    .join()
    .unwrap();
}
