//! Authenticated UAC handoff. The ordinary caller owns the journal and the
//! installation lock until the elevated confirmation/results window has closed.
use super::*;
use fs2::FileExt;

pub struct SecurityLifetime {
    _install: crate::update::ProcessLock,
    _ui: crate::update::ProcessLock,
    _lease: fs::File,
}
impl SecurityLifetime {
    pub fn acquire() -> Result<Self, String> {
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let install = exe.parent().ok_or("Не найдена папка установки")?;
        let install_lock =
            crate::update::ProcessLock::acquire(&crate::update::update_mutex_name(install))?;
        let ui = crate::update::ProcessLock::acquire("Local\\ChillHub.SecurityUi")?;
        let lease = open_lease(
            &journal_path()?.with_file_name("security-session.lock"),
            true,
            false,
        )?;
        Ok(Self {
            _install: install_lock,
            _ui: ui,
            _lease: lease,
        })
    }
}

fn open_lease(path: &Path, create: bool, exclusive: bool) -> Result<fs::File, String> {
    let parent = path.parent().ok_or("Не найдена папка сессии")?;
    let _guard =
        crate::manifest::DirectoryGuard::acquire(parent, create).map_err(|e| e.to_string())?;
    crate::manifest::check_no_links(path).map_err(|e| e.to_string())?;
    let file = fs::OpenOptions::new()
        .read(true)
        .write(create)
        .create(create)
        .truncate(false)
        .open(path)
        .map_err(|e| e.to_string())?;
    if exclusive {
        file.try_lock_exclusive()
    } else {
        FileExt::try_lock_shared(&file)
    }
    .map_err(|_| "Сначала завершите настройку защиты или обновление лаунчера")?;
    Ok(file)
}

pub(crate) fn update_lease(config: &Path) -> Result<fs::File, String> {
    open_lease(&config.join("security-session.lock"), true, true)
}

#[derive(Serialize, Deserialize)]
struct Envelope {
    token: String,
    request: SecurityRequest,
    lease_path: PathBuf,
}

fn validate_token(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|b| b.is_ascii_hexdigit())
}

#[cfg(windows)]
mod windows {
    use super::*;
    use std::{
        os::windows::{
            ffi::OsStrExt,
            io::{AsRawHandle, FromRawHandle},
        },
        sync::{Arc, Mutex},
    };
    use windows_sys::Win32::{
        Foundation::{HANDLE, INVALID_HANDLE_VALUE, LocalFree, WAIT_OBJECT_0},
        Security::{
            Authorization::{
                ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
            },
            GetTokenInformation, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER, TokenUser,
        },
        Storage::FileSystem::{
            CreateFileW, FILE_FLAG_FIRST_PIPE_INSTANCE, OPEN_EXISTING, PIPE_ACCESS_DUPLEX,
        },
        System::{
            Pipes::{
                ConnectNamedPipe, CreateNamedPipeW, GetNamedPipeClientProcessId,
                GetNamedPipeServerProcessId, PIPE_NOWAIT, PIPE_REJECT_REMOTE_CLIENTS,
                SetNamedPipeHandleState,
            },
            Threading::{
                GetCurrentProcess, GetProcessId, INFINITE, OpenProcess, OpenProcessToken,
                PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, QueryFullProcessImageNameW,
                WaitForSingleObject,
            },
        },
        UI::{
            Shell::{SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, ShellExecuteExW},
            WindowsAndMessaging::SW_SHOWNORMAL,
        },
    };
    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(Some(0)).collect()
    }
    fn handle(file: &fs::File) -> HANDLE {
        file.as_raw_handle() as _
    }
    fn error() -> String {
        std::io::Error::last_os_error().to_string()
    }

    fn current_sid() -> Result<String, String> {
        let mut token = std::ptr::null_mut();
        if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
            return Err(error());
        }
        let token = unsafe { fs::File::from_raw_handle(token as _) };
        let mut size = 0;
        unsafe {
            GetTokenInformation(
                handle(&token),
                TokenUser,
                std::ptr::null_mut(),
                0,
                &mut size,
            );
        }
        let mut buffer = vec![0usize; (size as usize).div_ceil(std::mem::size_of::<usize>())];
        if unsafe {
            GetTokenInformation(
                handle(&token),
                TokenUser,
                buffer.as_mut_ptr().cast(),
                size,
                &mut size,
            )
        } == 0
        {
            return Err(error());
        }
        let user = unsafe { &*(buffer.as_ptr() as *const TOKEN_USER) };
        let mut text = std::ptr::null_mut();
        if unsafe { ConvertSidToStringSidW(user.User.Sid, &mut text) } == 0 {
            return Err(error());
        }
        let mut len = 0;
        unsafe {
            while *text.add(len) != 0 {
                len += 1;
            }
        }
        let result = String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(text, len) });
        unsafe {
            LocalFree(text.cast());
        }
        Ok(result)
    }

    fn create_pipe(name: &str) -> Result<fs::File, String> {
        let sddl = wide(&format!(
            "D:P(A;;GA;;;SY)(A;;GRGW;;;BA)(A;;GA;;;{})",
            current_sid()?
        ));
        let mut descriptor = std::ptr::null_mut();
        if unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                1,
                &mut descriptor,
                std::ptr::null_mut(),
            )
        } == 0
        {
            return Err(error());
        }
        let attrs = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor,
            bInheritHandle: 0,
        };
        let path = wide(name);
        let raw = unsafe {
            CreateNamedPipeW(
                path.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                65536,
                65536,
                0,
                &attrs,
            )
        };
        unsafe {
            LocalFree(descriptor);
        }
        if raw == INVALID_HANDLE_VALUE {
            return Err(error());
        }
        Ok(unsafe { fs::File::from_raw_handle(raw as _) })
    }

    fn peer_process(pid: u32) -> Result<fs::File, String> {
        let process = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                0,
                pid,
            )
        };
        if process.is_null() {
            return Err(error());
        }
        let process = unsafe { fs::File::from_raw_handle(process as _) };
        let mut path = vec![0u16; 32768];
        let mut size = path.len() as u32;
        if unsafe { QueryFullProcessImageNameW(handle(&process), 0, path.as_mut_ptr(), &mut size) }
            == 0
        {
            return Err(error());
        }
        let peer = PathBuf::from(String::from_utf16_lossy(&path[..size as usize]));
        if !same_path(&peer, &std::env::current_exe().map_err(|e| e.to_string())?) {
            return Err("Подключился другой помощник".into());
        }
        Ok(process)
    }

    fn alive(process: &fs::File) -> bool {
        unsafe { WaitForSingleObject(handle(process), 0) != WAIT_OBJECT_0 }
    }

    // Byte mode + bounded individual JSON lines. Both sides remain nonblocking;
    // a peer process handle prevents PID reuse from extending the session.
    fn line(
        pipe: &mut fs::File,
        peer: &fs::File,
        deadline: Option<Instant>,
    ) -> Result<String, String> {
        let mut output = Vec::new();
        loop {
            let mut byte = [0u8];
            match pipe.read(&mut byte) {
                Ok(0) => return Err("Помощник закрыл соединение".into()),
                Ok(_) if byte[0] == b'\n' => {
                    return String::from_utf8(output)
                        .map_err(|_| "Некорректный ответ помощника".into());
                }
                Ok(_) => {
                    output.push(byte[0]);
                    if output.len() > 65536 {
                        return Err("Слишком большой ответ помощника".into());
                    }
                }
                Err(e) if e.raw_os_error() == Some(232) => {
                    if !alive(peer) {
                        return Err("Помощник завершился до передачи результата".into());
                    }
                    if deadline.is_some_and(|until| Instant::now() >= until) {
                        return Err("Помощник не ответил вовремя".into());
                    }
                    std::thread::sleep(Duration::from_millis(30));
                }
                Err(e) => return Err(e.to_string()),
            }
        }
    }

    fn send<T: Serialize>(pipe: &mut fs::File, value: &T) -> Result<(), String> {
        let mut bytes = serde_json::to_vec(value).map_err(|e| e.to_string())?;
        if bytes.len() > 65536 {
            return Err("Слишком большой запрос помощника".into());
        }
        bytes.push(b'\n');
        pipe.write_all(&bytes).map_err(|e| e.to_string())
    }

    pub struct ElevatedSession {
        pipe: Mutex<fs::File>,
        parent: fs::File,
        _lease: fs::File,
        request: SecurityRequest,
    }
    impl ElevatedSession {
        pub fn request(&self) -> SecurityRequest {
            self.request.clone()
        }
        pub fn apply_confirmed(&self) -> Result<Vec<ExclusionResult>, String> {
            if !alive(&self.parent) {
                return Err("Окно настройки закрыто. Начните настройку заново".into());
            }
            apply_with_sink(&self.request, |result| {
                let mut pipe = self.pipe.lock().map_err(|_| "Соединение недоступно")?;
                send(&mut pipe, result)
            })
        }
    }

    pub fn elevated_request_from_args(
        args: &[String],
    ) -> Result<Option<Arc<ElevatedSession>>, String> {
        if args.first().is_none_or(|a| a != "--elevated-session") {
            return Ok(None);
        }
        if args.len() != 4 || !is_elevated() || !validate_token(&args[2]) {
            return Err("Некорректная сессия настройки".into());
        }
        let suffix = args[1]
            .strip_prefix("ChillHub.Security.")
            .ok_or("Некорректная сессия настройки")?;
        if uuid::Uuid::parse_str(suffix).is_err() {
            return Err("Некорректная сессия настройки".into());
        }
        let pid: u32 = args[3].parse().map_err(|_| "Некорректный родитель")?;
        let parent = peer_process(pid)?;
        let path = wide(&format!("\\\\.\\pipe\\{}", args[1]));
        let raw = unsafe {
            CreateFileW(
                path.as_ptr(),
                0x80000000 | 0x40000000,
                0,
                std::ptr::null(),
                OPEN_EXISTING,
                0,
                std::ptr::null_mut(),
            )
        };
        if raw == INVALID_HANDLE_VALUE {
            return Err(error());
        }
        let mut pipe = unsafe { fs::File::from_raw_handle(raw as _) };
        let mode = PIPE_NOWAIT;
        if unsafe {
            SetNamedPipeHandleState(handle(&pipe), &mode, std::ptr::null(), std::ptr::null())
        } == 0
        {
            return Err(error());
        }
        let mut server = 0;
        if unsafe { GetNamedPipeServerProcessId(handle(&pipe), &mut server) } == 0 || server != pid
        {
            return Err("Изменился отправитель запроса".into());
        }
        let envelope: Envelope = serde_json::from_str(&line(
            &mut pipe,
            &parent,
            Some(Instant::now() + Duration::from_secs(30)),
        )?)
        .map_err(|_| "Некорректный запрос")?;
        if envelope
            .token
            .as_bytes()
            .iter()
            .zip(args[2].bytes())
            .fold(0u8, |v, (a, b)| v | (a ^ b))
            != 0
            || envelope.token.len() != args[2].len()
        {
            return Err("Запрос не совпал".into());
        }
        let request = SecurityRequest {
            paths: validate_for_operation(&envelope.request.paths, envelope.request.operation)?,
            operation: envelope.request.operation,
        };
        if envelope
            .lease_path
            .file_name()
            .is_none_or(|n| n != "security-session.lock")
        {
            return Err("Некорректная сессия настройки".into());
        }
        let lease = open_lease(&envelope.lease_path, false, false)?;
        Ok(Some(Arc::new(ElevatedSession {
            pipe: Mutex::new(pipe),
            parent,
            _lease: lease,
            request,
        })))
    }

    pub fn elevate_helper(request: &SecurityRequest) -> Result<(), String> {
        let journal = journal_path()?;
        let request = filter_owned_removals(request, &journal)?;
        prepare_directories(&request)?;
        let name = format!("ChillHub.Security.{}", uuid::Uuid::new_v4());
        let token = format!(
            "{}{}",
            uuid::Uuid::new_v4().simple(),
            uuid::Uuid::new_v4().simple()
        );
        let mut pipe = create_pipe(&format!("\\\\.\\pipe\\{name}"))?;
        let args = wide(&format!(
            "--elevated-session {name} {token} {}",
            std::process::id()
        ));
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let exe: Vec<_> = exe.as_os_str().encode_wide().chain(Some(0)).collect();
        let verb = wide("runas");
        let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
        info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
        info.fMask = SEE_MASK_NOCLOSEPROCESS;
        info.lpVerb = verb.as_ptr();
        info.lpFile = exe.as_ptr();
        info.lpParameters = args.as_ptr();
        info.nShow = SW_SHOWNORMAL;
        if unsafe { ShellExecuteExW(&mut info) } == 0 || info.hProcess.is_null() {
            return Err(
                "Настройка не запущена: подтверждение Windows отменено или недоступно".into(),
            );
        }
        let child = unsafe { fs::File::from_raw_handle(info.hProcess as _) };
        let result = (|| {
            let deadline = Instant::now() + Duration::from_secs(30);
            loop {
                let connected = unsafe { ConnectNamedPipe(handle(&pipe), std::ptr::null_mut()) };
                let code = std::io::Error::last_os_error().raw_os_error();
                let mut client = 0;
                if unsafe { GetNamedPipeClientProcessId(handle(&pipe), &mut client) } != 0
                    && client != 0
                {
                    break;
                }
                if connected == 0 && !matches!(code, Some(535 | 536 | 232)) {
                    return Err(error());
                }
                if Instant::now() >= deadline || !alive(&child) {
                    return Err("Помощник не подключился. Настройки не изменены".into());
                }
                std::thread::sleep(Duration::from_millis(30));
            }
            let mut client = 0;
            if unsafe { GetNamedPipeClientProcessId(handle(&pipe), &mut client) } == 0
                || client != unsafe { GetProcessId(handle(&child)) }
            {
                return Err("Подключился другой процесс".into());
            }
            send(
                &mut pipe,
                &Envelope {
                    token,
                    request: request.clone(),
                    lease_path: journal.with_file_name("security-session.lock"),
                },
            )?;
            let mut seen = std::collections::HashSet::new();
            let mut journal_error = None;
            while seen.len() < request.paths.len() {
                let result: ExclusionResult = serde_json::from_str(&line(&mut pipe, &child, None)?)
                    .map_err(|_| "Некорректный результат настройки")?;
                let key = path_key(Path::new(&result.path));
                if !request
                    .paths
                    .iter()
                    .any(|p| same_path(p, Path::new(&result.path)))
                    || !seen.insert(key)
                {
                    return Err("Помощник вернул неизвестный или повторный путь".into());
                }
                if let Err(error) = record_results(&journal, &[result]) {
                    journal_error.get_or_insert(error);
                }
            }
            match journal_error {
                Some(error) => Err(format!(
                    "Изменения могли примениться, но журнал сохранить не удалось: {error}"
                )),
                None => Ok(()),
            }
        })();
        // Even a malformed reply or a failed journal must not release the install
        // guard while the elevated window/process still owns its executable.
        drop(pipe);
        let waited = unsafe { WaitForSingleObject(handle(&child), INFINITE) };
        if waited != WAIT_OBJECT_0 {
            return Err("Не удалось дождаться закрытия помощника".into());
        }
        result
    }
}

#[cfg(windows)]
pub use windows::{ElevatedSession, elevate_helper, elevated_request_from_args};

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shared_helper_lifetimes_block_update_until_both_exit() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("security-session.lock");
        let parent = open_lease(&path, true, false).unwrap();
        let elevated = open_lease(&path, false, false).unwrap();
        assert!(update_lease(temp.path()).is_err());
        drop(parent);
        assert!(update_lease(temp.path()).is_err());
        drop(elevated);
        assert!(update_lease(temp.path()).is_ok());
    }
    #[test]
    fn session_tokens_are_fixed_size_hex() {
        assert!(validate_token(&"ab".repeat(32)));
        for value in [
            String::new(),
            "x".repeat(64),
            "a".repeat(63),
            "a".repeat(65),
        ] {
            assert!(!validate_token(&value));
        }
    }
}
