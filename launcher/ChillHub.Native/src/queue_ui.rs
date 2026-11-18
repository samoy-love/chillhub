//! Display-only queue accounting. Disk reuse is never reported as network traffic.
use chillhub_native::{
    queue::{QueueItem, QueueState, TransferOperation},
    transfer::{SyncScope, TransferStage},
    ui::{AppWindow, QueueRow},
};
use slint::{Image, Model, ModelRc, SharedString, VecModel};
use std::{
    collections::{HashMap, HashSet},
    time::{Duration, Instant},
};

#[derive(Default)]
pub(crate) struct Speeds(HashMap<String, Sample>);
struct Sample {
    at: Instant,
    bytes: u64,
    work: u64,
    samples: u32,
    rate: f64,
    work_rate: f64,
}
impl Speeds {
    pub fn update(&mut self, tasks: &[QueueItem], now: Instant) {
        let measuring = tasks.iter().filter_map(|task| {
            task.progress
                .as_ref()
                .filter(|p| {
                    task.state == QueueState::Running
                        && matches!(
                            p.stage,
                            TransferStage::Downloading | TransferStage::Verifying
                        )
                })
                .map(|p| (task, p))
        });
        self.0
            .retain(|id, _| measuring.clone().any(|(task, _)| &task.id == id));
        // Verification between file transfers belongs to the same sample window.
        // It contributes elapsed time but its UI does not display network figures.
        for (task, p) in measuring {
            let sample = self.0.entry(task.id.clone()).or_insert(Sample {
                at: now,
                bytes: p.network_bytes,
                work: p.completed_bytes,
                samples: 0,
                rate: 0.,
                work_rate: 0.,
            });
            if p.network_bytes < sample.bytes || p.completed_bytes < sample.work {
                *sample = Sample {
                    at: now,
                    bytes: p.network_bytes,
                    work: p.completed_bytes,
                    samples: 0,
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
        if !downloading(task) {
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

fn localized_size(bytes: u64) -> String {
    size(bytes).replace('.', ",")
}
pub(crate) fn state_label(task: &QueueItem) -> &'static str {
    match task.state {
        QueueState::Waiting => "В очереди",
        QueueState::Running => match task.progress.as_ref().map(|p| p.stage) {
            Some(TransferStage::Checking) => "Проверяем файлы",
            Some(TransferStage::Downloading) => "Скачиваем",
            Some(TransferStage::Verifying) => "Проверяем скачанное",
            Some(TransferStage::Activating) => "Применяем изменения",
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
pub(crate) fn stage_progress(task: &QueueItem) -> Option<f32> {
    let p = task
        .progress
        .as_ref()
        .filter(|_| task.state == QueueState::Running)?;
    match p.stage {
        TransferStage::Checking if p.total_files > 0 => {
            Some(p.completed_files.min(p.total_files) as f32 / p.total_files as f32)
        }
        TransferStage::Downloading | TransferStage::Verifying if p.total_bytes > 0 => {
            Some((p.completed_bytes as f64 / p.total_bytes as f64).clamp(0., 1.) as f32)
        }
        _ => None,
    }
}
pub(crate) fn can_remove(state: QueueState) -> bool {
    matches!(
        state,
        QueueState::Completed | QueueState::Failed | QueueState::Cancelled
    )
}
fn can_reorder(state: QueueState) -> bool {
    matches!(state, QueueState::Waiting | QueueState::Paused)
}

fn depends_on(task: &QueueItem, prerequisite: &QueueItem) -> bool {
    task.request.depends_on.as_deref() == Some(prerequisite.id.as_str())
}

fn prerequisite_pending(task: &QueueItem, live: &[&QueueItem]) -> bool {
    task.request.depends_on.is_some() && live.iter().any(|item| depends_on(task, item))
}

fn can_move(live: &[&QueueItem], index: usize, up: bool) -> bool {
    let task = live[index];
    if !can_reorder(task.state) {
        return false;
    }
    if up {
        index.checked_sub(1).is_some_and(|previous| {
            let neighbor = live[previous];
            !depends_on(task, neighbor)
                && (neighbor.state != QueueState::Running || !prerequisite_pending(task, live))
        })
    } else {
        live.get(index + 1)
            .is_some_and(|next| !depends_on(next, task))
    }
}

/// The insertion point for a one-position move. A missing insertion point
/// inside the result means appending after the last nonterminal item.
pub(crate) fn move_target(tasks: &[QueueItem], id: &str, up: bool) -> Option<Option<String>> {
    let live = tasks
        .iter()
        .filter(|t| !can_remove(t.state))
        .collect::<Vec<_>>();
    let index = live.iter().position(|t| t.id == id)?;
    if !can_move(&live, index, up) {
        return None;
    }
    if up {
        Some(Some(live[index - 1].id.clone()))
    } else {
        Some(live.get(index + 2).map(|t| t.id.clone()))
    }
}

pub(crate) fn active_task(tasks: &[QueueItem]) -> Option<&QueueItem> {
    tasks
        .iter()
        .find(|t| {
            matches!(
                t.state,
                QueueState::Running | QueueState::Pausing | QueueState::Cancelling
            )
        })
        .or_else(|| {
            tasks
                .iter()
                .find(|t| t.state == QueueState::NeedsConfirmation)
        })
        .or_else(|| {
            tasks
                .iter()
                .find(|t| t.state == QueueState::Paused)
                .filter(|_| !tasks.iter().any(|t| t.state == QueueState::Waiting))
        })
}

fn downloading(task: &QueueItem) -> bool {
    task.state == QueueState::Running
        && task
            .progress
            .as_ref()
            .is_some_and(|p| p.stage == TransferStage::Downloading)
}

fn remaining(seconds: Option<u64>) -> String {
    let Some(seconds) = seconds else {
        return "Уточняем…".into();
    };
    let minutes = seconds.div_ceil(60);
    match (minutes / 60, minutes % 60) {
        (0, _) if seconds < 60 => "< 1 мин".into(),
        (0, minutes) => format!("≈ {minutes} мин"),
        (hours, 0) => format!("≈ {hours} ч"),
        (hours, minutes) => format!("≈ {hours} ч {minutes} мин"),
    }
}

fn operation(task: &QueueItem) -> &'static str {
    match (task.report.operation, task.request.scope) {
        (Some(TransferOperation::Install), SyncScope::WholeRoot) => "Установка игры",
        (Some(TransferOperation::Install), SyncScope::OwnFilesOnly) => "Установка модпака",
        (Some(TransferOperation::Update), SyncScope::WholeRoot) => "Обновление файлов игры",
        (Some(TransferOperation::Update), SyncScope::OwnFilesOnly) => "Обновление модпака",
        (Some(TransferOperation::Verify), _) => "Проверка файлов",
        (_, SyncScope::WholeRoot) => "Игра",
        (_, SyncScope::OwnFilesOnly) => "Модпак",
    }
}

fn row(task: &QueueItem, speeds: &Speeds, title: SharedString, icon: Image) -> QueueRow {
    let downloading = downloading(task);
    let mut row = QueueRow {
        id: task.id.as_str().into(),
        title,
        icon,
        operation: operation(task).into(),
        state: state_label(task).into(),
        detail: match task.state {
            QueueState::Pausing | QueueState::Cancelling => {
                "Завершаем текущую операцию с файлом.".into()
            }
            QueueState::Paused if task.progress.as_ref().is_some_and(|p| p.network_bytes > 0) => {
                "Уже скачанные данные сохранены.".into()
            }
            _ => task.error.as_deref().unwrap_or_default().into(),
        },
        downloading,
        processing: matches!(
            task.state,
            QueueState::Running | QueueState::Pausing | QueueState::Cancelling
        ),
        can_pause: matches!(task.state, QueueState::Running | QueueState::Waiting),
        can_resume: matches!(
            task.state,
            QueueState::Paused | QueueState::Failed | QueueState::Cancelled
        ),
        can_cancel: matches!(
            task.state,
            QueueState::Waiting
                | QueueState::Running
                | QueueState::Paused
                | QueueState::NeedsConfirmation
        ),
        can_confirm: task.state == QueueState::NeedsConfirmation,
        can_reorder: can_reorder(task.state),
        can_remove: can_remove(task.state),
        security_blocked: task.state == QueueState::Failed
            && task.report.error_code.as_deref() == Some("sync_security_blocked"),
        ..Default::default()
    };
    if let Some(p) = task
        .progress
        .as_ref()
        .filter(|_| task.state == QueueState::Running)
    {
        row.file = p.current_file.as_str().into();
        if let Some(fraction) = stage_progress(task) {
            row.progress = fraction;
            row.progress_label = format!("{}%", (fraction * 100.).floor()).into();
        }
        if downloading {
            let rate = speeds.rate(task);
            let processed = if p.total_bytes > 0 {
                row.processed_total = localized_size(p.total_bytes).into();
                p.completed_bytes.min(p.total_bytes)
            } else {
                p.completed_bytes
            };
            row.processed = localized_size(processed).into();
            row.transferred = localized_size(p.network_bytes).into();
            row.speed = if rate >= 1. {
                format!("{}/с", localized_size(rate as u64)).into()
            } else {
                "—".into()
            };
            // The transfer can reuse bytes on disk. Use the rate of completed work for ETA.
            row.eta = if p.total_bytes > 0 && p.completed_bytes >= p.total_bytes {
                "—".into()
            } else {
                remaining(speeds.eta(task)).into()
            };
        }
    }
    row
}

pub(crate) type Rows = (QueueRow, Vec<QueueRow>, Vec<QueueRow>);

pub(crate) fn rows(
    tasks: &[QueueItem],
    speeds: &Speeds,
    identify: impl Fn(&str) -> (SharedString, Image),
) -> Rows {
    let active_id = active_task(tasks).map(|t| t.id.as_str());
    let live = tasks
        .iter()
        .filter(|task| !can_remove(task.state))
        .collect::<Vec<_>>();
    let mut live_index = 0;
    let mut active = QueueRow::default();
    let mut pending = Vec::new();
    let mut history = Vec::new();
    for task in tasks {
        let (title, icon) = identify(&task.request.game_id);
        let mut item = row(task, speeds, title, icon);
        if !item.can_remove {
            if task.state == QueueState::Waiting && prerequisite_pending(task, &live) {
                item.state = "После установки игры".into();
            }
            item.can_move_up = can_move(&live, live_index, true);
            item.can_move_down = can_move(&live, live_index, false);
            live_index += 1;
        }
        if Some(task.id.as_str()) == active_id {
            active = item;
        } else if item.can_remove {
            history.push(item);
        } else {
            pending.push(item);
        }
    }
    (active, pending, history)
}

pub(crate) fn apply_rows(ui: &AppWindow, (active, pending, history): Rows) {
    if !same_row(&ui.get_queue_active(), &active) {
        ui.set_queue_active(active);
    }
    sync_rows(ui.get_queue(), pending, |model| ui.set_queue(model));
    sync_rows(ui.get_queue_history(), history, |model| {
        ui.set_queue_history(model)
    });
}

fn same_row(a: &QueueRow, b: &QueueRow) -> bool {
    let QueueRow {
        id,
        title,
        icon,
        operation,
        state,
        detail,
        progress,
        progress_label,
        file,
        downloading,
        processing,
        processed,
        processed_total,
        transferred,
        speed,
        eta,
        can_pause,
        can_resume,
        can_cancel,
        can_confirm,
        can_reorder,
        can_move_up,
        can_move_down,
        can_remove,
        security_blocked,
    } = a;
    id == &b.id
        && title == &b.title
        // Slint's default Image compares unequal even to another default.
        && (icon == &b.icon || icon.size().width == 0 && b.icon.size().width == 0)
        && operation == &b.operation
        && state == &b.state
        && detail == &b.detail
        && *progress == b.progress
        && progress_label == &b.progress_label
        && file == &b.file
        && *downloading == b.downloading
        && *processing == b.processing
        && processed == &b.processed
        && processed_total == &b.processed_total
        && transferred == &b.transferred
        && speed == &b.speed
        && eta == &b.eta
        && *can_pause == b.can_pause
        && *can_resume == b.can_resume
        && *can_cancel == b.can_cancel
        && *can_confirm == b.can_confirm
        && *can_reorder == b.can_reorder
        && *can_move_up == b.can_move_up
        && *can_move_down == b.can_move_down
        && *can_remove == b.can_remove
        && *security_blocked == b.security_blocked
}

fn sync_rows(model: ModelRc<QueueRow>, items: Vec<QueueRow>, bind: impl FnOnce(ModelRc<QueueRow>)) {
    let Some(rows) = model.as_any().downcast_ref::<VecModel<QueueRow>>() else {
        bind(ModelRc::new(VecModel::from(items)));
        return;
    };
    if model.row_count() != items.len() || model.iter().zip(&items).any(|(a, b)| a.id != b.id) {
        let ids = items
            .iter()
            .map(|item| item.id.as_str())
            .collect::<HashSet<_>>();
        for index in (0..rows.row_count()).rev() {
            if rows
                .row_data(index)
                .is_some_and(|item| !ids.contains(item.id.as_str()))
            {
                rows.remove(index);
            }
        }
    }
    for (index, item) in items.into_iter().enumerate() {
        match rows.row_data(index) {
            Some(previous) if same_row(&previous, &item) => (),
            Some(previous) if previous.id == item.id => rows.set_row_data(index, item),
            _ => {
                if let Some(previous) = ((index + 1)..rows.row_count()).find(|&position| {
                    rows.row_data(position)
                        .is_some_and(|previous| previous.id == item.id)
                }) {
                    rows.remove(previous);
                }
                rows.insert(index, item);
            }
        }
    }
}
#[cfg(test)]
#[path = "queue_ui_tests.rs"]
mod tests;
