//! Explicit install-and-play requests survive queue reordering, never application restarts.
use super::*;

#[derive(Default)]
pub(crate) struct Pending {
    jobs: Vec<Job>,
    scheduled: bool,
}
struct Job {
    game: GameRecord,
    target: LaunchTarget,
    root: String,
    tasks: Vec<String>,
    completed: std::collections::HashSet<String>,
}

impl Job {
    fn matches_installation(&self, directory: &std::path::Path) -> bool {
        chillhub_native::paths::path_identity(directory).as_deref() == Some(self.root.as_str())
    }
}

impl Pending {
    pub(super) fn observe_finished(&mut self, task: &QueueItem) {
        if matches!(task.state, QueueState::Cancelled | QueueState::Failed) {
            self.jobs.retain(|job| !job.tasks.contains(&task.id));
        } else if task.state == QueueState::Completed {
            for job in &mut self.jobs {
                if job.tasks.contains(&task.id) {
                    job.completed.insert(task.id.clone());
                }
            }
        }
    }

    fn take_ready(&mut self, tasks: &[QueueItem]) -> Option<Job> {
        let mut index = 0;
        while index < self.jobs.len() {
            let job = &self.jobs[index];
            // Cancellation, failure, deletion or a reset revokes automatic launch.
            // A later manual retry must not unexpectedly start a game.
            let states = job
                .tasks
                .iter()
                .map(|id| {
                    tasks
                        .iter()
                        .find(|task| task.id == *id)
                        .map(|task| task.state)
                })
                .collect::<Vec<_>>();
            if states.is_empty()
                || states.iter().any(|state| {
                    matches!(
                        state,
                        None | Some(
                            QueueState::Cancelled | QueueState::Failed | QueueState::Cancelling
                        )
                    )
                })
            {
                self.jobs.remove(index);
            } else if job.completed.len() == job.tasks.len()
                && states
                    .iter()
                    .all(|state| *state == Some(QueueState::Completed))
            {
                return Some(self.jobs.remove(index));
            } else {
                index += 1;
            }
        }
        None
    }
}

fn should_offer_install(shortcut_file_exists: bool, action: &LaunchAction) -> bool {
    !shortcut_file_exists && !matches!(action, LaunchAction::Play | LaunchAction::Unavailable)
}

pub(super) fn offer(ui: &AppWindow, state: &State, request: &ShortcutRequest) {
    // Old shortcuts have no executable path. As in the original launcher they
    // simply select a card; they never authorize an install or immediate launch.
    let Some(shortcut_file) = request.exe_path.clone() else {
        return;
    };
    let Some((game, target)) = selected(state) else {
        return;
    };
    if game.game_id != request.game_id || target.via_steam() || !game.has_latest {
        return;
    }
    let (services, generation) = {
        let s = state.borrow();
        (s.services.clone(), s.catalog_generation)
    };
    let captured = game.clone();
    background(
        ui,
        move || {
            services
                .option(&captured, target)
                .is_ok_and(|option| should_offer_install(shortcut_file.is_file(), &option.action))
        },
        move |ui, state, offer| {
            if !offer
                || ui.get_operation_busy()
                || state.borrow().catalog_generation != generation
                || !selected(state).is_some_and(|(selected, choice)| {
                    selected.game_id == game.game_id && choice == target
                })
            {
                return;
            }
            let title = game.title.clone();
            confirm(ui, state, Confirmation::InstallShortcut(game, target),
            &format!("«{title}» не найдена на диске"),
            "Папку игры переместили или удалили. Скачаем выбранную копию и запустим её после завершения установки. Отмена загрузки также отменит запуск.".into(),
            "Скачать и запустить");
        },
    );
}

pub(super) fn install(ui: &AppWindow, state: &State, game: GameRecord, target: LaunchTarget) {
    if target.via_steam() {
        return;
    }
    let services = state.borrow().services.clone();
    let captured = game.clone();
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        move || {
            let option = services.option(&captured, target)?;
            if option.ready() {
                return Ok(None);
            }
            if !option.available() {
                return Err(option.note);
            }
            install_ui::preflight(&option.game_dir).map_err(|error| error.to_string())?;
            let root = chillhub_native::paths::path_identity(&option.game_dir)
                .ok_or("Не удалось проверить папку установки игры")?;
            let tasks = services.enqueue_game(&captured, target, None, false, false)?;
            Ok(Some((root, tasks)))
        },
        move |ui, state, result| {
            ui.set_operation_busy(false);
            match result {
                Ok(Some((root, tasks))) => {
                    // A tiny/cached installation may have finished before the
                    // callback could register the user's request.
                    let completion = state
                        .borrow()
                        .services
                        .queue
                        .snapshot()
                        .into_iter()
                        .filter(|task| {
                            task.state == QueueState::Completed && tasks.contains(&task.id)
                        })
                        .map(|task| task.id)
                        .collect();
                    state.borrow_mut().shortcut_installs.jobs.push(Job {
                        game,
                        target,
                        root,
                        tasks,
                        completed: completion,
                    });
                    open_panel(ui, state, "downloads");
                    pump(ui, state);
                }
                Ok(None) => {
                    toast(ui, state, "Копия уже найдена. Нажмите «Играть».");
                    refresh_choice(ui, state);
                }
                Err(error) => report_failure(ui, state, "shortcut-install", &error),
            }
        },
    );
}

pub(super) fn pump(ui: &AppWindow, state: &State) {
    if state.borrow().shortcut_installs.jobs.is_empty() {
        return;
    }
    if ui.get_operation_busy() {
        let mut s = state.borrow_mut();
        if s.shortcut_installs.scheduled {
            return;
        }
        s.shortcut_installs.scheduled = true;
        let weak = ui.as_weak();
        let state = Rc::downgrade(state);
        Timer::single_shot(Duration::from_millis(300), move || {
            if let (Some(ui), Some(state)) = (weak.upgrade(), state.upgrade()) {
                state.borrow_mut().shortcut_installs.scheduled = false;
                pump(&ui, &state);
            }
        });
        return;
    }
    let tasks = state.borrow().services.queue.snapshot();
    let Some(job) = state.borrow_mut().shortcut_installs.take_ready(&tasks) else {
        return;
    };
    let services = state.borrow().services.clone();
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        move || {
            let option = services.option(&job.game, job.target)?;
            if !job.matches_installation(&option.game_dir)
                || option.action != LaunchAction::Play
                || services.queue.has_pending_for_root(&option.game_dir)
            {
                return Err("Установка завершена, но состояние игры изменилось. Проверьте её карточку перед запуском.".to_string());
            }
            services.maintenance_check(&LaunchAction::Play)?;
            match services.launch_service().launch(&job.game, job.target)? {
                LaunchOutcome::Started(session) => {
                    record_catalog_game_launch(&services, job.game, job.target, &option.game_dir);
                    Ok(session)
                }
                LaunchOutcome::SteamPending { warning, .. } => Err(warning.into()),
            }
        },
        |ui, state, result| {
            ui.set_operation_busy(false);
            match result {
                Ok(session) => show_game_started(ui, state, session),
                Err(error) => report_failure(ui, state, "shortcut-launch", &error),
            }
            refresh_choice(ui, state);
            pump(ui, state);
        },
    );
}

#[cfg(test)]
#[path = "shortcut_install_tests.rs"]
mod tests;
