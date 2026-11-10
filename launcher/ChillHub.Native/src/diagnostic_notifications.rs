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
mod tests {
    use super::*;
    #[test]
    fn notices_are_profile_scoped_and_quota_notice_is_not_repeated_each_tick() {
        let temp = tempfile::tempdir().unwrap();
        let (tx, rx) = std::sync::mpsc::channel();
        subscribe(temp.path(), move |notice| {
            let _ = tx.send(notice);
        });
        let delayed = ReportNotice::Delayed {
            retry_after_seconds: 61,
        };
        notify(&temp.path().join("other"), ReportNotice::Sent, None);
        assert!(rx.try_recv().is_err());
        notify(temp.path(), delayed.clone(), Some(1));
        notify(temp.path(), delayed.clone(), Some(1));
        assert_eq!(rx.try_recv().unwrap(), delayed);
        assert!(rx.try_recv().is_err());
        notify(temp.path(), ReportNotice::Sent, None);
        assert_eq!(rx.try_recv().unwrap(), ReportNotice::Sent);
        notify(temp.path(), delayed, Some(2));
        assert!(rx.try_recv().is_ok());
    }
}
