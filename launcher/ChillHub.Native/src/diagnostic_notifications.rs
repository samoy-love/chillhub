use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ReportNotice {
    Sent,
    Delayed { retry_after_seconds: u64 },
}
impl ReportNotice {
    pub fn message(&self) -> String {
        match self {
            Self::Sent => "Отчёт об ошибке автоматически отправлен.".into(),
            Self::Delayed {
                retry_after_seconds,
            } => format!(
                "Отправка отчётов временно ограничена. Повторим через {} мин.",
                retry_after_seconds.div_ceil(60).max(1)
            ),
        }
    }
}
struct Listener {
    callback: Arc<dyn Fn(ReportNotice) + Send + Sync>,
    suppressed_window: Option<i64>,
}
static LISTENERS: Mutex<BTreeMap<PathBuf, Listener>> = Mutex::new(BTreeMap::new());

/// One weak-window listener per profile; delivery never holds the registry lock.
pub fn subscribe(data: &Path, callback: impl Fn(ReportNotice) + Send + Sync + 'static) {
    if let Ok(mut listeners) = LISTENERS.lock() {
        if listeners.len() >= 64 && !listeners.contains_key(data) {
            listeners.pop_first();
        }
        listeners.insert(
            data.to_owned(),
            Listener {
                callback: Arc::new(callback),
                suppressed_window: None,
            },
        );
    }
}
pub(crate) fn notify(data: &Path, notice: ReportNotice, quota_window: Option<i64>) {
    let callback = {
        let Ok(mut listeners) = LISTENERS.lock() else {
            return;
        };
        let Some(listener) = listeners.get_mut(data) else {
            return;
        };
        if matches!(notice, ReportNotice::Delayed { .. }) {
            if listener.suppressed_window == quota_window {
                return;
            }
            listener.suppressed_window = quota_window;
        }
        listener.callback.clone()
    };
    callback(notice);
}

#[cfg(test)]
#[path = "diagnostic_notifications_tests.rs"]
mod tests;
