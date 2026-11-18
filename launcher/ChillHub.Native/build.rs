fn main() {
    #[cfg(feature = "launcher-ui")]
    build_previews().expect("Prepare library artwork previews");
    println!("cargo:rerun-if-env-changed=CHILLHUB_UI_QA");
    let config = slint_build::CompilerConfiguration::new()
        .with_debug_info(std::env::var_os("CHILLHUB_UI_QA").is_some());
    let ui_root = if std::env::var_os("CARGO_FEATURE_LAUNCHER_UI").is_some() {
        Some("ui/root.slint")
    } else if std::env::var_os("CARGO_FEATURE_HELPER_UI").is_some() {
        Some("ui/helper-root.slint")
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

// The first frame of a selection must not wait for disk, network or full-size decoding.
// Embed only small JPEGs. Full-resolution artwork stays in the external media pack.
#[cfg(feature = "launcher-ui")]
fn build_previews() -> Result<(), Box<dyn std::error::Error>> {
    use std::{env, fmt::Write, fs, path::PathBuf};

    println!("cargo:rerun-if-changed=art");
    let output = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    let mut entries = Vec::new();
    for game in fs::read_dir("art")? {
        let game = game?;
        if !game.file_type()?.is_dir() {
            continue;
        }
        for file in fs::read_dir(game.path())? {
            let path = file?.path();
            if path.extension().and_then(|ext| ext.to_str()) != Some("webp") {
                continue;
            }
            let name = path
                .file_stem()
                .unwrap()
                .to_str()
                .ok_or("invalid artwork name")?;
            let bounds = if name == "hero" {
                (320, 160)
            } else {
                (120, 75)
            };
            let preview = image::open(&path)?.thumbnail(bounds.0, bounds.1).to_rgb8();
            let mut jpeg = Vec::new();
            image::codecs::jpeg::JpegEncoder::new_with_quality(&mut jpeg, 77)
                .encode_image(&preview)?;
            entries.push((
                game.file_name()
                    .into_string()
                    .map_err(|_| "invalid game id")?,
                name.to_owned(),
                jpeg,
            ));
        }
    }
    entries.sort_by(|a, b| (&a.0, &a.1).cmp(&(&b.0, &b.1)));
    let mut index = String::from("match (game, name) {\n");
    let mut bytes = Vec::new();
    for (game, name, jpeg) in entries {
        let start = bytes.len();
        bytes.extend(jpeg);
        writeln!(
            index,
            "({game:?}, {name:?}) => Some(&BYTES[{start}..{}]),",
            bytes.len()
        )?;
    }
    index.push_str("_ => None,\n}");
    fs::write(output.join("card-previews.bin"), bytes)?;
    fs::write(output.join("card-previews.rs"), index)?;
    Ok(())
}
