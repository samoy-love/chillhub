//! Display-only queue accounting. Disk reuse is never reported as network traffic.
use chillhub_native::{
    queue::{QueueItem, QueueState},
    transfer::TransferStage,
};
use std::{
    collections::HashMap,
    time::{Duration, Instant},
};

#[derive(Default)]
pub(crate) struct Speeds(HashMap<String, Sample>);
struct Sample {
    at: Instant,
    bytes: u64,
    work: u64,
    samples: u32,
    stage: TransferStage,
    rate: f64,
    work_rate: f64,
}
impl Speeds {
    pub fn update(&mut self, tasks: &[QueueItem], now: Instant) {
        self.0.retain(|id, _| {
            tasks
                .iter()
                .any(|t| &t.id == id && t.state == QueueState::Running)
        });
        for task in tasks.iter().filter(|t| t.state == QueueState::Running) {
            let Some(p) = &task.progress else { continue };
            let sample = self.0.entry(task.id.clone()).or_insert(Sample {
                at: now,
                bytes: p.network_bytes,
                work: p.completed_bytes,
                samples: 0,
                stage: p.stage,
                rate: 0.,
                work_rate: 0.,
            });
            if sample.stage != p.stage
                || p.network_bytes < sample.bytes
                || p.completed_bytes < sample.work
            {
                *sample = Sample {
                    at: now,
                    bytes: p.network_bytes,
                    work: p.completed_bytes,
                    samples: 0,
                    stage: p.stage,
                    rate: 0.,
                    work_rate: 0.,
                };
            } else if now.saturating_duration_since(sample.at) >= Duration::from_millis(500) {
                sample.rate = (p.network_bytes - sample.bytes) as f64
                    / now.duration_since(sample.at).as_secs_f64();
                let work_rate = (p.completed_bytes - sample.work) as f64
                    / now.duration_since(sample.at).as_secs_f64();
                sample.work_rate = if sample.samples == 0 {
                    work_rate
                } else {
                    sample.work_rate * 0.65 + work_rate * 0.35
                };
                sample.work = p.completed_bytes;
                sample.samples += 1;
                sample.at = now;
                sample.bytes = p.network_bytes;
            }
        }
    }
    pub fn rate(&self, task: &QueueItem) -> f64 {
        if task.state != QueueState::Running {
            return 0.;
        }
        self.0.get(&task.id).map_or(0., |s| s.rate)
    }
    pub fn eta(&self, task: &QueueItem) -> Option<u64> {
        if task.state != QueueState::Running {
            return None;
        }
        let p = task.progress.as_ref()?;
        if p.stage != TransferStage::Downloading || p.total_bytes <= p.completed_bytes {
            return None;
        }
        let sample = self.0.get(&task.id)?;
        if sample.samples < 2 || sample.work_rate < 1. {
            return None;
        }
        Some(((p.total_bytes - p.completed_bytes) as f64 / sample.work_rate).ceil() as u64)
    }
}
pub(crate) fn size(bytes: u64) -> String {
    if bytes >= 1 << 30 {
        format!("{:.1} ГБ", bytes as f64 / (1_u64 << 30) as f64)
    } else if bytes >= 1 << 20 {
        format!("{:.1} МБ", bytes as f64 / (1_u64 << 20) as f64)
    } else if bytes >= 1024 {
        format!("{:.1} КБ", bytes as f64 / 1024.)
    } else {
        format!("{bytes} Б")
    }
}
pub(crate) fn state_label(task: &QueueItem) -> &'static str {
    match task.state {
        QueueState::Waiting => "В очереди",
        QueueState::Running => match task.progress.as_ref().map(|p| p.stage) {
            Some(TransferStage::Checking) => "Проверяем файлы",
            Some(TransferStage::Downloading) => "Скачиваем",
            Some(TransferStage::Verifying) => "Проверяем скачанное",
            Some(TransferStage::Activating) => "Применяем обновление",
            Some(TransferStage::Completed) => "Завершаем",
            None => "Подготавливаем",
        },
        QueueState::Pausing => "Приостанавливаем…",
        QueueState::Paused => "На паузе",
        QueueState::Cancelling => "Отменяем…",
        QueueState::Completed => "Готово",
        QueueState::Failed => "Не удалось завершить",
        QueueState::Cancelled => "Отменено",
        QueueState::NeedsConfirmation => "Требуется подтверждение",
    }
}
pub(crate) fn progress(task: &QueueItem) -> f32 {
    if task.state == QueueState::Completed {
        return 1.;
    }
    task.progress
        .as_ref()
        .filter(|p| p.total_bytes > 0)
        .map_or(0., |p| {
            (p.completed_bytes as f64 / p.total_bytes as f64).clamp(0., 1.) as f32
        })
}
pub(crate) fn detail(task: &QueueItem, rate: f64, eta: Option<u64>) -> String {
    let mut parts = Vec::new();
    if let Some(error) = &task.error
        && !error.is_empty()
    {
        parts.push(error.clone());
    }
    // A stopping operation must not continue to promise a live rate or ETA.
    if matches!(task.state, QueueState::Pausing | QueueState::Cancelling) {
        parts.push("Ждём безопасного завершения текущей операции с файлом.".into());
        return parts.join("\n");
    }
    if let Some(p) = &task.progress {
        if !p.current_file.is_empty() {
            parts.push(p.current_file.clone());
        }
        if p.total_files > 0 {
            parts.push(format!(
                "Файлы: {} из {}",
                p.completed_files.min(p.total_files),
                p.total_files
            ));
        }
        if p.total_bytes > 0 {
            parts.push(format!(
                "Обработано {} из {}",
                size(p.completed_bytes),
                size(p.total_bytes)
            ));
        }
        if p.network_bytes > 0 || p.stage == TransferStage::Downloading {
            let speed = if rate >= 1. && task.state == QueueState::Running {
                format!(" · {}/с", size(rate as u64))
            } else {
                String::new()
            };
            parts.push(format!("Из интернета: {}{speed}", size(p.network_bytes)));
            // ETA is based on completed disk work, including local reuse, never
            // on full disk work divided by network speed.
            if task.state == QueueState::Running && p.stage == TransferStage::Downloading {
                parts.push(match eta {
                    Some(seconds) if seconds < 60 => "По текущему темпу — меньше минуты".into(),
                    Some(seconds) if seconds < 3600 => {
                        format!("По текущему темпу — около {} мин", seconds.div_ceil(60))
                    }
                    Some(seconds) => format!(
                        "По текущему темпу — около {} ч {} мин",
                        seconds / 3600,
                        (seconds % 3600).div_ceil(60)
                    ),
                    None => "Оставшееся время уточняется по мере обработки файлов".into(),
                });
            }
        }
        if p.reused_bytes > 0 {
            parts.push(format!("Использовано с диска: {}", size(p.reused_bytes)));
        }
    }
    parts.join("\n")
}
#[cfg(test)]
mod tests {
    use super::*;
    use chillhub_native::transfer::{TransferEvent, TransferRequest};
    fn task() -> QueueItem {
        QueueItem {
            id: "one".into(),
            request: TransferRequest::default(),
            state: QueueState::Running,
            progress: Some(TransferEvent {
                stage: TransferStage::Checking,
                current_file: "a.bin".into(),
                completed_bytes: 100,
                total_bytes: 1000,
                network_bytes: 0,
                reused_bytes: 100,
                completed_files: 1,
                total_files: 2,
            }),
            outcome: None,
            error: None,
            cleanup_paths: vec![],
            report: Default::default(),
        }
    }
    #[test]
    fn checking_and_cancellation_never_pretend_to_download() {
        let mut t = task();
        assert_eq!(state_label(&t), "Проверяем файлы");
        assert!(!detail(&t, 9000., None).contains("/с"));
        t.state = QueueState::Cancelling;
        assert!(!detail(&t, 9000., Some(60)).contains("100"));
    }
    #[test]
    fn network_rate_resets_after_pause_and_stage_change() {
        let mut t = task();
        t.progress.as_mut().unwrap().stage = TransferStage::Downloading;
        let mut s = Speeds::default();
        let now = Instant::now();
        s.update(&[t.clone()], now);
        t.progress.as_mut().unwrap().network_bytes = 1000;
        s.update(&[t.clone()], now + Duration::from_secs(1));
        assert_eq!(s.rate(&t), 1000.);
        t.state = QueueState::Paused;
        s.update(&[t.clone()], now + Duration::from_secs(2));
        assert_eq!(s.rate(&t), 0.);
        t.state = QueueState::Running;
        s.update(&[t.clone()], now + Duration::from_secs(3));
        assert_eq!(s.rate(&t), 0.);
    }
    #[test]
    fn unknown_network_total_is_not_invented() {
        let mut t = task();
        t.progress.as_mut().unwrap().stage = TransferStage::Downloading;
        assert!(detail(&t, 100., None).contains("Из интернета: 0 Б"));
        assert!(detail(&t, 100., None).contains("уточняется"));
    }
    #[test]
    fn local_reuse_eta_does_not_divide_disk_work_by_network_speed() {
        let mut t = task();
        t.progress.as_mut().unwrap().stage = TransferStage::Downloading;
        let mut speeds = Speeds::default();
        let now = Instant::now();
        speeds.update(&[t.clone()], now);
        t.progress.as_mut().unwrap().completed_bytes = 300;
        speeds.update(&[t.clone()], now + Duration::from_secs(1));
        assert!(speeds.eta(&t).is_none());
        t.progress.as_mut().unwrap().completed_bytes = 500;
        speeds.update(&[t.clone()], now + Duration::from_secs(2));
        assert_eq!(speeds.rate(&t), 0.);
        assert_eq!(speeds.eta(&t), Some(3));
        t.state = QueueState::Paused;
        assert!(speeds.eta(&t).is_none());
    }
}
