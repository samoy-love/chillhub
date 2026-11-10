use crate::{net::ApiClient, paths::atomic_write};
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
    sync::{Mutex, TryLockError},
};

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct FeedbackDraft {
    pub name: String,
    pub contact: String,
    #[serde(rename = "Type")]
    pub kind: String,
    pub comment: String,
    pub attach_logs: bool,
    pub system: Option<BTreeMap<String, String>>,
    pub id: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SendResult {
    Sent,
    QueuedPersisted,
    Rejected(String),
    RateLimited(u64),
    LocalSaveFailed(String),
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Quota {
    count: u32,
    window_start_utc: DateTime<Utc>,
}

pub struct FeedbackService {
    api: ApiClient,
    data: PathBuf,
    lock: Mutex<()>,
}
impl FeedbackService {
    pub fn new(api: ApiClient, data: PathBuf) -> Self {
        Self {
            api,
            data,
            lock: Mutex::new(()),
        }
    }
    fn queue_path(&self) -> PathBuf {
        self.data.join("feedback_queue.json")
    }
    fn load(&self) -> Result<Vec<FeedbackDraft>, String> {
        use std::io::Read;
        match fs::File::open(self.queue_path()) {
            Ok(file) => {
                const LIMIT: u64 = 8 * 1024 * 1024;
                if file.metadata().map_err(|e| e.to_string())?.len() > LIMIT {
                    return Err("Очередь сообщений слишком велика.".into());
                }
                let mut bytes = Vec::new();
                file.take(LIMIT + 1)
                    .read_to_end(&mut bytes)
                    .map_err(|e| e.to_string())?;
                if bytes.len() as u64 > LIMIT {
                    return Err("Очередь сообщений слишком велика.".into());
                }
                serde_json::from_slice(&bytes).map_err(|_| {
                    "Очередь сообщений повреждена. Файл сохранён для восстановления.".into()
                })
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(vec![]),
            Err(e) => Err(e.to_string()),
        }
    }
    fn save(&self, queue: &[FeedbackDraft]) -> Result<(), String> {
        let bytes = serde_json::to_vec_pretty(queue).map_err(|e| e.to_string())?;
        atomic_write(&self.queue_path(), &bytes).map_err(|e| e.to_string())
    }
    fn quota(&self) -> Result<Option<u64>, String> {
        let path = self.data.join("report_manual_rl.json");
        let now = Utc::now();
        let mut q: Quota = fs::read(&path)
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or(Quota {
                count: 0,
                window_start_utc: now,
            });
        let elapsed = now.signed_duration_since(q.window_start_utc).num_seconds();
        if !(0..300).contains(&elapsed) {
            q = Quota {
                count: 0,
                window_start_utc: now,
            };
        }
        if q.count >= 5 {
            return Ok(Some((300 - elapsed).max(1) as u64));
        }
        q.count += 1;
        atomic_write(&path, &serde_json::to_vec(&q).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())?;
        Ok(None)
    }
    pub fn submit(&self, mut draft: FeedbackDraft) -> SendResult {
        let Ok(_guard) = self.lock.lock() else {
            return SendResult::LocalSaveFailed("Очередь сообщений недоступна.".into());
        };
        draft.comment = draft.comment.trim().into();
        if draft.comment.is_empty()
            || draft.comment.chars().count() > 8000
            || draft.name.len() > 240
            || draft.contact.len() > 512
        {
            return SendResult::Rejected(
                "Добавьте описание до 8000 символов и проверьте контактные данные.".into(),
            );
        }
        match self.quota() {
            Ok(Some(seconds)) => return SendResult::RateLimited(seconds),
            Err(e) => return SendResult::LocalSaveFailed(e),
            _ => {}
        }
        draft.id = uuid::Uuid::new_v4().to_string();
        if draft.system.is_none() {
            draft.system = Some(system_info());
        }
        match self.send(&draft) {
            Some(result) => result,
            None => {
                let mut queue = match self.load() {
                    Ok(q) => q,
                    Err(e) => return SendResult::LocalSaveFailed(e),
                };
                if queue.len() >= 100 {
                    return SendResult::LocalSaveFailed(
                        "Очередь сообщений заполнена. Сохраните текст и отправьте позже.".into(),
                    );
                }
                queue.push(draft);
                match self.save(&queue) {
                    Ok(()) => SendResult::QueuedPersisted,
                    Err(e) => SendResult::LocalSaveFailed(e),
                }
            }
        }
    }
    fn send(&self, draft: &FeedbackDraft) -> Option<SendResult> {
        let endpoint = match self.api.resolve("feedback/submit") {
            Ok(u) => u,
            Err(e) => return Some(SendResult::Rejected(e.to_string())),
        };
        let logs = if draft.attach_logs {
            diagnostics(&self.data)
        } else {
            String::new()
        };
        let system = draft.system.clone().unwrap_or_else(system_info);
        let body = serde_json::json!({"name":draft.name,"contact":draft.contact,"type":draft.kind,"comment":draft.comment,
            "attachLogs":draft.attach_logs,"logs":logs,"system":system});
        match self
            .api
            .client
            .post(endpoint)
            .header("X-Request-ID", &draft.id)
            .json(&body)
            .send()
        {
            Ok(response) if response.status().is_success() => Some(SendResult::Sent),
            Ok(response) if response.status().as_u16() == 429 => Some(SendResult::RateLimited(
                response
                    .headers()
                    .get("Retry-After")
                    .and_then(|s| s.to_str().ok())
                    .and_then(|s| s.parse().ok())
                    .unwrap_or(300),
            )),
            Ok(response) if response.status().is_client_error() => {
                Some(SendResult::Rejected(format!(
                    "Сервер отклонил сообщение (код {}). Текст сохранён в форме.",
                    response.status().as_u16()
                )))
            }
            _ => None,
        }
    }
    pub fn flush(&self) -> Result<usize, String> {
        let _guard = match self.lock.try_lock() {
            Ok(guard) => guard,
            Err(TryLockError::WouldBlock) => return Ok(0),
            Err(TryLockError::Poisoned(_)) => return Err("Очередь сообщений недоступна".into()),
        };
        let mut queue = self.load()?;
        let mut delivered = 0;
        let mut processed = 0;
        while !queue.is_empty() && processed < 5 {
            processed += 1;
            if queue[0].id.is_empty() {
                queue[0].id = uuid::Uuid::new_v4().to_string();
                self.save(&queue)?;
            }
            match self.send(&queue[0]) {
                Some(SendResult::Sent) => {
                    queue.remove(0);
                    self.save(&queue)?;
                    delivered += 1;
                }
                Some(SendResult::Rejected(reason)) => {
                    let rejected = queue.remove(0);
                    let path = self
                        .data
                        .join(format!("feedback-rejected-{}.json", uuid::Uuid::new_v4()));
                    atomic_write(
                        &path,
                        &serde_json::to_vec(&serde_json::json!({"draft":rejected,"reason":reason}))
                            .map_err(|e| e.to_string())?,
                    )
                    .map_err(|e| e.to_string())?;
                    self.save(&queue)?;
                }
                _ => break,
            }
        }
        Ok(delivered)
    }
    pub fn pending(&self) -> Result<usize, String> {
        self.load().map(|q| q.len())
    }
}

pub fn diagnostics(data: &Path) -> String {
    crate::diagnostic_bundle::collect(data)
}

fn system_info() -> BTreeMap<String, String> {
    BTreeMap::from([
        ("os".into(), crate::diagnostics::os_version()),
        ("arch".into(), std::env::consts::ARCH.into()),
        ("appVersion".into(), env!("CARGO_PKG_VERSION").into()),
        ("runtime".into(), "Rust + Slint".into()),
    ])
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_message_is_not_saved_or_sent() {
        let temp = tempfile::tempdir().unwrap();
        let service = FeedbackService::new(
            ApiClient::new("http://127.0.0.1:9").unwrap(),
            temp.path().into(),
        );
        assert!(matches!(
            service.submit(FeedbackDraft::default()),
            SendResult::Rejected(_)
        ));
        assert!(!service.queue_path().exists());
    }
    #[test]
    fn rejected_queue_is_not_misreported_as_empty() {
        let temp = tempfile::tempdir().unwrap();
        let service = FeedbackService::new(
            ApiClient::new("http://127.0.0.1:9").unwrap(),
            temp.path().into(),
        );
        fs::write(service.queue_path(), b"broken").unwrap();
        assert!(service.pending().is_err());
        assert_eq!(fs::read(service.queue_path()).unwrap(), b"broken");
    }

    #[test]
    fn simultaneous_retry_does_not_wait_or_spend_another_batch() {
        let temp = tempfile::tempdir().unwrap();
        let service = FeedbackService::new(
            ApiClient::new("http://127.0.0.1:9").unwrap(),
            temp.path().into(),
        );
        let _held = service.lock.lock().unwrap();
        assert_eq!(service.flush().unwrap(), 0);
    }

    #[test]
    fn legacy_feedback_keeps_the_system_from_when_it_was_written() {
        let temp = tempfile::tempdir().unwrap();
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let service = FeedbackService::new(
            ApiClient::new(&format!("http://{}", server.server_addr())).unwrap(),
            temp.path().into(),
        );
        let legacy = br#"[{"Name":"Player","Contact":"","Type":"bug","Comment":"old report","AttachLogs":false,"System":{"os":"Windows 10","appVersion":"1.7.20"}}]"#;
        fs::write(service.queue_path(), legacy).unwrap();
        let worker = std::thread::spawn(move || {
            let mut request = server
                .recv_timeout(std::time::Duration::from_secs(5))
                .unwrap()
                .unwrap();
            let body: serde_json::Value = serde_json::from_reader(request.as_reader()).unwrap();
            assert_eq!(body["system"]["appVersion"], "1.7.20");
            assert_eq!(body["system"]["os"], "Windows 10");
            assert_eq!(body["comment"], "old report");
            request.respond(tiny_http::Response::empty(200)).unwrap();
        });
        assert_eq!(service.flush().unwrap(), 1);
        assert_eq!(service.pending().unwrap(), 0);
        worker.join().unwrap();
    }
}
