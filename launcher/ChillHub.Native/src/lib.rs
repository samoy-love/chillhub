pub mod changelog;
pub mod config;
pub mod diagnostic_bundle;
pub mod diagnostic_notifications;
pub mod diagnostics;
pub mod feedback;
pub mod folder_picker;
#[cfg(feature = "helper-ui")]
pub mod helper_runtime;
pub mod installed_apps;
pub mod integrity;
pub mod launch;
pub mod library;
pub mod manifest;
#[cfg(feature = "launcher-ui")]
pub mod media;
pub mod model;
pub mod net;
pub mod paths;
pub mod playtime;
pub mod queue;
pub mod security;
pub mod service;
#[cfg(feature = "helper-ui")]
pub mod setup;
pub mod shortcuts;
pub mod transfer;
pub mod update;
#[cfg(feature = "web-video")]
pub mod web_video;
#[cfg(any(feature = "launcher-ui", feature = "helper-ui"))]
pub mod ui {
    slint::include_modules!();
}
