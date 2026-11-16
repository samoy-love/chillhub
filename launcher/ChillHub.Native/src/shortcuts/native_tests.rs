use super::*;

#[test]
fn unicode_buffers_reject_nul_and_truncation() {
    assert!(wide(OsStr::new("a\0b")).is_err());
    assert!(wide(OsStr::new(&"x".repeat(MAX_WIDE))).is_err());
    assert!(text(&[1, 2]).is_err());
    assert_eq!(
        text(&wide(OsStr::new("游戏 🎮")).unwrap()).unwrap(),
        "游戏 🎮"
    );
}

pub(in crate::shortcuts) fn details(path: &Path) -> Result<(PathBuf, PathBuf, i32), String> {
    let path = path.to_owned();
    in_apartment(move || {
        let link = load(&path)?;
        let mut working = vec![0; MAX_WIDE];
        let mut icon = vec![0; MAX_WIDE];
        let mut index = 0;
        unsafe {
            link.GetWorkingDirectory(&mut working)
                .map_err(|error| error.to_string())?;
            link.GetIconLocation(&mut icon, &mut index)
                .map_err(|error| error.to_string())?;
        }
        Ok((text(&working)?.into(), text(&icon)?.into(), index))
    })
}
