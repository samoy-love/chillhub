use std::{fs, path::Path, process::Command};

pub fn compile(source: &Path, executable: &Path) {
    fs::write(
        source,
        r#"#![windows_subsystem = "windows"]

fn main() {
    std::fs::write(
        "started.txt",
        std::env::current_dir().unwrap().to_string_lossy().as_bytes(),
    )
    .unwrap();
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
    while !std::path::Path::new("stop").exists() && std::time::Instant::now() < deadline {
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
}
"#,
    )
    .unwrap();
    let mut compiler = Command::new(std::env::var_os("RUSTC").unwrap_or_else(|| "rustc".into()));
    compiler
        .arg(source)
        .arg("--crate-name=launch_fixture")
        .args(["-C", "strip=symbols"])
        .arg("-o")
        .arg(executable);
    // A GNU override is irrelevant to MSVC, even if inherited from the shell.
    #[cfg(target_env = "gnu")]
    if let Some(linker) = std::env::var_os("CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER")
        .filter(|value| !value.is_empty())
    {
        compiler
            .arg("-C")
            .arg(format!("linker={}", linker.to_string_lossy()))
            .args(["-C", "linker-flavor=ld", "-C", "link-self-contained=yes"]);
        let dlltool = Path::new(&linker)
            .parent()
            .expect("GNU fixture linker must include its toolchain directory")
            .join("llvm-dlltool.exe");
        compiler
            .arg("-C")
            .arg(format!("dlltool={}", dlltool.display()));
    }
    let output = compiler.output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
}
