use super::{ActionPanel, UiAction};

#[test]
fn every_action_token_maps_to_its_known_route() {
    use UiAction::*;

    let actions = [
        ("choose-games-folder", ChooseGamesFolder),
        ("copy-game-path", CopyGamePath),
        ("open-modpack-source", OpenModpackSource),
        ("retry-catalog", RetryCatalog),
        ("delete-selected-orphans", DeleteSelectedOrphans),
        ("open-logs", OpenLogs),
        ("cancel-action", Cancel),
        ("confirm-action", Confirm),
        ("verify-game", VerifyGame),
        ("verify-mods", VerifyMods),
        ("open-game-folder", OpenGameFolder),
        ("delete-game", DeleteGame),
        ("version-install", InstallVersion),
        ("create-shortcut", CreateShortcut),
        ("security-check", CheckSecurity),
        ("security-diagnostics", CheckSecurity),
        ("security-helper", OpenSecurityHelper),
        ("security-open", OpenWindowsSecurity),
        ("windows-security", OpenWindowsSecurity),
        ("update-check", CheckUpdate),
        ("update-verify", VerifyUpdate),
        ("update-install", InstallUpdate),
    ];
    for (token, action) in actions {
        assert_eq!(UiAction::parse(token), Some(action), "{token}");
    }
    for (token, panel) in [
        ("feedback", ActionPanel::Feedback),
        ("changelog", ActionPanel::Changelog),
        ("update", ActionPanel::Update),
        ("security", ActionPanel::Security),
    ] {
        assert_eq!(UiAction::parse(token), Some(OpenPanel(panel)));
        assert_eq!(panel.token(), token);
    }
}

#[test]
fn malformed_and_unrelated_tokens_are_not_actions() {
    for token in [
        "",
        " verify-game",
        "verify-game ",
        "Verify-Game",
        "update_check",
        "choose-games-folder\n",
        "resume",
        "toggle",
        "manage",
    ] {
        assert_eq!(UiAction::parse(token), None, "{token:?}");
    }
}
