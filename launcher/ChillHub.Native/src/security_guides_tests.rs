use super::*;
#[test]
fn unknown_guide_cannot_open_a_link_or_copy_invalid_paths() {
    assert!(open_guide(usize::MAX).is_err());
    assert!(open_guide(guides().len() - 1).is_err());
    assert!(copy_paths(&[PathBuf::from("relative/path")]).is_err());
}

#[test]
fn vendor_aliases_are_case_insensitive_and_prefer_the_specific_product() {
    for (index, guide) in guides().iter().enumerate() {
        for alias in &guide.aliases {
            assert_eq!(matching_guide(&alias.to_uppercase()), index);
        }
    }
    assert_eq!(
        guides()[matching_guide("Bitdefender Total Security")].id,
        "bitdefender"
    );
    assert_eq!(
        guides()[matching_guide("Microsoft Defender Antivirus")].id,
        "defender"
    );
}

#[test]
#[cfg(windows)]
fn installed_vendor_discovery_is_sorted_unique_and_recognized() {
    // Read-only registry discovery: no product is installed or configured.
    let names = discovered_names();
    assert!(names.windows(2).all(|pair| pair[0] < pair[1]));
    for (index, name) in names.iter().enumerate() {
        assert!(matching_guide(name) < guides().len() - 1);
        assert!(
            !names[..index]
                .iter()
                .any(|other| other.eq_ignore_ascii_case(name))
        );
    }
    let recommended = recommended_guide();
    assert!(recommended < guides().len() - 1);
    if names.is_empty() {
        assert_eq!(recommended, 0);
    }
}

#[test]
fn all_vendor_guides_have_safe_official_links_and_known_aliases() {
    assert_eq!(guides().len(), 12);
    assert_eq!(guides()[matching_guide("ESET NOD32 Antivirus")].id, "eset");
    assert_eq!(guides()[matching_guide("unknown product")].id, "other");
    for guide in guides().iter().filter(|g| g.id != "other") {
        let url = url::Url::parse(&guide.url).unwrap();
        assert_eq!(url.scheme(), "https");
        assert!(url.username().is_empty());
        assert!(!guide.instructions.is_empty());
    }
    assert!(
        guides()
            .iter()
            .find(|g| g.id == "mcafee")
            .unwrap()
            .instructions
            .contains("не поддерживается")
    );
}
