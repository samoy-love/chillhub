fn main() {
    println!("cargo:rerun-if-env-changed=CHILLHUB_UI_QA");
    let config = slint_build::CompilerConfiguration::new()
        .with_debug_info(std::env::var_os("CHILLHUB_UI_QA").is_some());
    let ui_root = if std::env::var_os("CARGO_FEATURE_LAUNCHER_UI").is_some() {
        Some("ui/root.slint")
    } else if std::env::var_os("CARGO_FEATURE_HELPER_UI").is_some() {
        Some("ui/auxiliary.slint")
    } else {
        None
    };
    if let Some(ui_root) = ui_root {
        slint_build::compile_with_config(ui_root, config).expect("Compile the launcher interface");
    }
    println!("cargo:rerun-if-changed=../../scripts/app.ico");
    println!("cargo:rerun-if-changed=../../scripts/native-app.manifest");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut resources = winresource::WindowsResource::new();
        resources
            .set_icon("../../scripts/app.ico")
            .set_manifest_file("../../scripts/native-app.manifest")
            .set("ProductName", "Chill Hub")
            .set("FileDescription", "Chill Hub")
            .set("CompanyName", "samoy.love");
        if let Ok(dir) = std::env::var("CHILLHUB_LLVM_MINGW") {
            resources
                .set_windres_path(&format!("{dir}/x86_64-w64-mingw32-windres.exe"))
                .set_ar_path(&format!("{dir}/llvm-ar.exe"));
        }
        resources.compile().expect("Compile Windows resources");
    }
}
