#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum UiAction {
    ChooseGamesFolder,
    CopyGamePath,
    OpenModpackSource,
    RetryCatalog,
    DeleteSelectedOrphans,
    OpenLogs,
    OpenPanel(ActionPanel),
    Cancel,
    Confirm,
    VerifyGame,
    VerifyMods,
    OpenGameFolder,
    DeleteGame,
    InstallVersion,
    CreateShortcut,
    CheckSecurity,
    OpenSecurityHelper,
    OpenWindowsSecurity,
    CheckUpdate,
    VerifyUpdate,
    InstallUpdate,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum ActionPanel {
    Feedback,
    Changelog,
    Update,
    Security,
}

impl ActionPanel {
    pub(super) fn token(self) -> &'static str {
        match self {
            Self::Feedback => "feedback",
            Self::Changelog => "changelog",
            Self::Update => "update",
            Self::Security => "security",
        }
    }
}

impl UiAction {
    pub(super) fn parse(value: &str) -> Option<Self> {
        Some(match value {
            "choose-games-folder" => Self::ChooseGamesFolder,
            "copy-game-path" => Self::CopyGamePath,
            "open-modpack-source" => Self::OpenModpackSource,
            "retry-catalog" => Self::RetryCatalog,
            "delete-selected-orphans" => Self::DeleteSelectedOrphans,
            "open-logs" => Self::OpenLogs,
            "feedback" => Self::OpenPanel(ActionPanel::Feedback),
            "changelog" => Self::OpenPanel(ActionPanel::Changelog),
            "update" => Self::OpenPanel(ActionPanel::Update),
            "security" => Self::OpenPanel(ActionPanel::Security),
            "cancel-action" => Self::Cancel,
            "confirm-action" => Self::Confirm,
            "verify-game" => Self::VerifyGame,
            "verify-mods" => Self::VerifyMods,
            "open-game-folder" => Self::OpenGameFolder,
            "delete-game" => Self::DeleteGame,
            "version-install" => Self::InstallVersion,
            "create-shortcut" => Self::CreateShortcut,
            "security-check" | "security-diagnostics" => Self::CheckSecurity,
            "security-helper" => Self::OpenSecurityHelper,
            "security-open" | "windows-security" => Self::OpenWindowsSecurity,
            "update-check" => Self::CheckUpdate,
            "update-verify" => Self::VerifyUpdate,
            "update-install" => Self::InstallUpdate,
            _ => return None,
        })
    }
}

#[cfg(test)]
#[path = "ui_action_tests.rs"]
mod tests;
