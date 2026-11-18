use super::*;

const BASE: &str = "https://example.test/content/game/1/files";

fn plan(sizes: impl IntoIterator<Item = u64>) -> TransferPlan {
    let files: Vec<_> = sizes
        .into_iter()
        .enumerate()
        .map(|(index, size)| ManifestFile {
            path: format!("dir/{index}.bin"),
            size,
            ..Default::default()
        })
        .collect();
    TransferPlan {
        manifest: Manifest {
            files: files.clone(),
            ..Default::default()
        },
        downloads: files,
        to_delete: vec![],
        required_free_bytes: 0,
        total_download_bytes: 0,
        total_manifest_bytes: 0,
        hash_mismatches: 0,
        pack_order_safe: true,
        request: TransferRequest {
            content_base_url: BASE.into(),
            ..Default::default()
        },
    }
}

fn entry(files: Vec<usize>) -> SmallPack {
    SmallPack {
        sha256: "7890abcdef123456".repeat(4),
        files,
    }
}

fn check(plan: &TransferPlan, entry: &SmallPack) -> Option<Pack> {
    let needed = plan
        .downloads
        .iter()
        .enumerate()
        .map(|(index, file)| (file.path.as_str(), index))
        .collect();
    candidate(entry, plan, &needed)
}

#[test]
fn pack_url_is_content_addressed_on_the_original_origin() {
    let hash = &entry(vec![]).sha256;
    let expected = format!("https://example.test/content/game/1/packs/{hash}.bin");
    for base in [BASE.to_string(), format!("{BASE}/?ticket=one#fragment")] {
        assert_eq!(pack_url(&base, hash).unwrap().as_str(), expected);
    }
    for invalid in [
        "https://example.test/",
        "https://example.test/content/",
        "https://example.test/content/files//",
        "https://user:pass@example.test/content/files/",
        "file:///content/files/",
        "relative/files/",
    ] {
        assert!(pack_url(invalid, hash).is_none(), "{invalid}");
    }
}

#[test]
fn pack_bounds_apply_to_each_file_and_the_whole_group() {
    let sizes = plan([1; MAX_FILES + 1]);
    for (count, valid) in [(7, false), (8, true), (128, true), (129, false)] {
        assert_eq!(check(&sizes, &entry((0..count).collect())).is_some(), valid);
    }
    let limits = plan([MAX_FILE_BYTES; 9]);
    assert_eq!(
        check(&limits, &entry((0..8).collect())).unwrap().size,
        MAX_PACK_BYTES as usize
    );
    assert!(check(&limits, &entry((0..9).collect())).is_none());
    for invalid in [0, MAX_FILE_BYTES + 1, u64::MAX] {
        let mut sizes = [1; MIN_FILES];
        sizes[1] = invalid;
        assert!(check(&plan(sizes), &entry((0..MIN_FILES).collect())).is_none());
    }
}

#[test]
fn pack_indices_are_unique_needed_manifest_indices_in_declared_order() {
    let mut plan = plan([1; 9]);
    let mut pack = entry((1..9).rev().collect());
    assert_eq!(check(&plan, &pack).unwrap().indices, pack.files);
    pack.files[0] = 9;
    assert!(check(&plan, &pack).is_none());
    pack.files[0] = pack.files[1];
    assert!(check(&plan, &pack).is_none());
    pack.files[0] = 8;
    plan.downloads.remove(4);
    assert!(check(&plan, &pack).is_none());
}

#[test]
fn invalid_hashes_and_overlapping_groups_cannot_claim_normal_work() {
    let mut plan = plan([1; 9]);
    let pack = entry((0..8).collect());
    for hash in [pack.sha256.to_uppercase(), "1".repeat(63), "z".repeat(64)] {
        let mut invalid = pack.clone();
        invalid.sha256 = hash;
        assert!(check(&plan, &invalid).is_none());
    }
    plan.manifest.small_packs = vec![pack, entry((1..9).collect())];
    let work = schedule(&plan);
    assert!(matches!(&work[..], [Work::Pack(_), Work::File(8)]));
    plan.pack_order_safe = false;
    assert!(
        schedule(&plan)
            .iter()
            .all(|item| matches!(item, Work::File(_)))
    );
}

#[test]
fn a_failed_pack_requeues_every_member_for_the_original_workers() {
    let mut plan = plan([1; MIN_FILES]);
    plan.manifest
        .small_packs
        .push(entry((0..MIN_FILES).collect()));
    let queue = WorkQueue::new(&plan);
    let Some(Job::Pack(pack)) = queue.take() else {
        panic!("expected one pack job");
    };
    std::thread::scope(|scope| {
        let files: Vec<_> = (0..MIN_FILES)
            .map(|_| {
                scope.spawn(|| match queue.take() {
                    Some(Job::File(index)) => index,
                    _ => panic!("expected individual fallback work"),
                })
            })
            .collect();
        queue.finish_pack(pack, false);
        let mut completed: Vec<_> = files.into_iter().map(|job| job.join().unwrap()).collect();
        completed.sort_unstable();
        assert_eq!(completed, (0..MIN_FILES).collect::<Vec<_>>());
    });
    assert!(queue.take().is_none());
}

#[test]
fn provisional_pack_credit_is_reversed_without_touching_other_files() {
    let stats = Progress::new(100, 8, 0);
    stats.completed.store(5, Ordering::Relaxed);
    {
        let mut credit = PackCredit {
            stats: &stats,
            bytes: 0,
        };
        credit.set(30);
        assert_eq!(stats.completed.load(Ordering::Relaxed), 35);
        credit.set(12);
        assert_eq!(stats.completed.load(Ordering::Relaxed), 17);
    }
    assert_eq!(stats.completed.load(Ordering::Relaxed), 5);
}
