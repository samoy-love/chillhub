//! Optional content-addressed bundles for small files in a fresh installation.
use super::*;
use crate::manifest::SmallPack;
use std::collections::VecDeque;

const MIN_FILES: usize = 8;
const MAX_FILES: usize = 128;
const MAX_FILE_BYTES: u64 = 64 * 1024;
const MAX_PACK_BYTES: u64 = 512 * 1024;

pub(super) enum Work {
    File(usize),
    Pack(Pack),
}

pub(super) struct Pack {
    url: Url,
    sha256: String,
    indices: Vec<usize>,
    size: usize,
}

pub(super) enum Job<'a> {
    File(usize),
    Pack(&'a Pack),
}

pub(super) struct WorkQueue {
    initial: Vec<Work>,
    next: AtomicUsize,
    stopped: AtomicBool,
    fallback: Option<Fallback>,
}

pub(super) struct StopOnPanic<'a>(pub(super) &'a WorkQueue);

impl Drop for StopOnPanic<'_> {
    fn drop(&mut self) {
        if std::thread::panicking() {
            // Waiting workers must leave the scope if, for example, a
            // caller's progress callback panics during a pack request.
            self.0.stop();
        }
    }
}

struct PackFallback {
    pending: usize,
    files: VecDeque<usize>,
}

struct Fallback {
    state: Mutex<PackFallback>,
    changed: Condvar,
}

impl WorkQueue {
    pub(super) fn new(plan: &TransferPlan) -> Self {
        let initial = schedule(plan);
        let pending = initial
            .iter()
            .filter(|item| matches!(item, Work::Pack(_)))
            .count();
        Self {
            initial,
            next: AtomicUsize::new(0),
            stopped: AtomicBool::new(false),
            fallback: (pending != 0).then(|| Fallback {
                state: Mutex::new(PackFallback {
                    pending,
                    files: VecDeque::new(),
                }),
                changed: Condvar::new(),
            }),
        }
    }

    pub(super) fn take(&self) -> Option<Job<'_>> {
        if self.is_stopped() {
            return None;
        }
        match self.initial.get(self.next.fetch_add(1, Ordering::Relaxed)) {
            Some(Work::File(index)) => return Some(Job::File(*index)),
            Some(Work::Pack(pack)) => return Some(Job::Pack(pack)),
            None => (),
        }
        let fallback = self.fallback.as_ref()?;
        let mut state = fallback
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        loop {
            if self.is_stopped() {
                return None;
            }
            if let Some(index) = state.files.pop_front() {
                return Some(Job::File(index));
            }
            if state.pending == 0 {
                return None;
            }
            state = fallback
                .changed
                .wait(state)
                .unwrap_or_else(|error| error.into_inner());
        }
    }

    pub(super) fn finish_pack(&self, pack: &Pack, installed: bool) {
        let fallback = self.fallback.as_ref().expect("a pack has fallback state");
        let mut state = fallback
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        state.pending -= 1;
        if !installed {
            // Failed bundles rejoin the regular work queue; they do not use
            // a single worker to download every member in sequence.
            state.files.extend(pack.indices.iter().copied());
        }
        fallback.changed.notify_all();
    }

    pub(super) fn stop(&self) {
        if let Some(fallback) = &self.fallback {
            let _state = fallback
                .state
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            self.stopped.store(true, Ordering::Release);
            fallback.changed.notify_all();
        } else {
            self.stopped.store(true, Ordering::Release);
        }
    }

    fn is_stopped(&self) -> bool {
        self.stopped.load(Ordering::Acquire)
    }
}

pub(super) fn schedule(plan: &TransferPlan) -> Vec<Work> {
    let order = download_order(&plan.downloads);
    if !plan.pack_order_safe
        || !plan.request.donor_roots.is_empty()
        || plan.manifest.small_packs.is_empty()
    {
        return order.into_iter().map(Work::File).collect();
    }
    let needed: HashMap<_, _> = plan
        .downloads
        .iter()
        .enumerate()
        .map(|(index, file)| (file.path.as_str(), index))
        .collect();
    let mut by_file = vec![None; plan.downloads.len()];
    let mut packs = Vec::new();
    for entry in &plan.manifest.small_packs {
        let Some(pack) = candidate(entry, plan, &needed) else {
            continue;
        };
        if pack.indices.iter().any(|&index| by_file[index].is_some()) {
            continue;
        }
        for &index in &pack.indices {
            by_file[index] = Some(packs.len());
        }
        packs.push(Some(pack));
    }
    order
        .into_iter()
        .filter_map(|index| match by_file[index] {
            Some(pack) => packs[pack].take().map(Work::Pack),
            None => Some(Work::File(index)),
        })
        .collect()
}

fn candidate(
    entry: &SmallPack,
    plan: &TransferPlan,
    needed: &HashMap<&str, usize>,
) -> Option<Pack> {
    if !(MIN_FILES..=MAX_FILES).contains(&entry.files.len())
        || entry.sha256.len() != 64
        || !entry
            .sha256
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return None;
    }
    let mut size = 0;
    let mut indices = Vec::with_capacity(entry.files.len());
    let mut seen = HashSet::with_capacity(entry.files.len());
    for &index in &entry.files {
        let file = plan.manifest.files.get(index)?;
        let &download = needed.get(file.path.as_str())?;
        if !(1..=MAX_FILE_BYTES).contains(&file.size)
            || file.size != plan.downloads[download].size
            || !seen.insert(download)
        {
            return None;
        }
        size += file.size;
        if size > MAX_PACK_BYTES {
            return None;
        }
        indices.push(download);
    }
    Some(Pack {
        url: pack_url(&plan.request.content_base_url, &entry.sha256)?,
        sha256: entry.sha256.clone(),
        indices,
        size: size as usize,
    })
}

fn pack_url(base: &str, sha256: &str) -> Option<Url> {
    let mut url = checked_url(base).ok()?;
    let directory = url.path().strip_suffix('/').unwrap_or(url.path());
    if directory.rsplit('/').next()? != "files" {
        return None;
    }
    url.set_query(None);
    url.set_fragment(None);
    url.path_segments_mut()
        .ok()?
        .pop_if_empty()
        .pop()
        .push("packs")
        .push(&format!("{sha256}.bin"));
    Some(url)
}

impl Pack {
    fn fresh(
        &self,
        plan: &TransferPlan,
        request: &TransferRequest,
        control: &TransferControl,
    ) -> Result<bool> {
        for &index in &self.indices {
            control.checkpoint()?;
            let file = &plan.downloads[index];
            let Ok(destination) = path_in(&request.root, &file.path) else {
                return Ok(false);
            };
            let mut temporary = destination.file_name().unwrap().to_os_string();
            temporary.push(".part");
            let part = destination.with_file_name(temporary);
            if !matches!(manifest::exists_without_links(&destination), Ok(false))
                || !matches!(manifest::exists_without_links(&part), Ok(false))
            {
                return Ok(false);
            }
        }
        Ok(true)
    }

    fn members<'a>(
        &'a self,
        plan: &'a TransferPlan,
        bytes: &'a [u8],
    ) -> impl Iterator<Item = (&'a ManifestFile, &'a [u8])> {
        let mut offset = 0;
        self.indices.iter().map(move |&index| {
            let file = &plan.downloads[index];
            let end = offset + file.size as usize;
            let payload = &bytes[offset..end];
            offset = end;
            (file, payload)
        })
    }
}

struct PackCredit<'a> {
    stats: &'a Progress,
    bytes: u64,
}

impl PackCredit<'_> {
    fn set(&mut self, bytes: u64) {
        set_credit(self.stats, &mut self.bytes, bytes);
    }
}

impl Drop for PackCredit<'_> {
    fn drop(&mut self) {
        self.set(0);
    }
}

impl TransferEngine {
    #[allow(clippy::too_many_arguments)]
    pub(super) fn transfer_pack(
        &self,
        request: &TransferRequest,
        plan: &TransferPlan,
        pack: &Pack,
        control: &TransferControl,
        stats: &Progress,
        event: &(dyn Fn(TransferEvent) + Sync),
        hashes: &Mutex<HashCache>,
        worker: &mut TransferWorker,
        queue: &WorkQueue,
    ) -> Result<bool> {
        if !pack.fresh(plan, request, control)? {
            return Ok(false);
        }
        let mut credit = PackCredit { stats, bytes: 0 };
        let bytes = match self.fetch_pack(
            pack,
            plan,
            control,
            event,
            &mut worker.buffer,
            &mut credit,
            queue,
        ) {
            Ok(bytes) => bytes,
            Err(TransferError::Cancelled) => return Err(TransferError::Cancelled),
            // Bundles are an optimization. Every member has a normal URL.
            Err(_) => return Ok(false),
        };
        for (file, payload) in pack.members(plan, &bytes) {
            if queue.is_stopped() {
                return Err(TransferError::Cancelled);
            }
            // Transfer the provisional download credit to the standard file
            // path, which credits only what it has written and verified.
            credit.set(credit.bytes - file.size);
            self.transfer_file(
                request,
                &plan.manifest,
                file,
                control,
                stats,
                event,
                hashes,
                worker,
                Some(payload),
            )
            .map_err(|error| antivirus_error(error, &file.path))?;
        }
        Ok(true)
    }

    #[allow(clippy::too_many_arguments)]
    fn fetch_pack(
        &self,
        pack: &Pack,
        plan: &TransferPlan,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
        buffer: &mut [u8],
        credit: &mut PackCredit<'_>,
        queue: &WorkQueue,
    ) -> Result<Vec<u8>> {
        control.checkpoint()?;
        if queue.is_stopped() {
            return Err(TransferError::Cancelled);
        }
        let mut response = self
            .client
            .get(pack.url.clone())
            .header(ACCEPT_ENCODING, "identity")
            .send()?;
        if response.status() != StatusCode::OK
            || response
                .content_length()
                .is_some_and(|n| n != pack.size as u64)
        {
            return Err(TransferError::Protocol(
                "incorrect small pack response".into(),
            ));
        }
        // Unverified bundle bytes stay in memory. An interrupted request can
        // require fetching up to one pack again per worker.
        let mut bytes = Vec::with_capacity(pack.size);
        let first = &plan.downloads[pack.indices[0]].path;
        loop {
            control.checkpoint()?;
            if queue.is_stopped() {
                return Err(TransferError::Cancelled);
            }
            // Read one byte past the limit to detect a body without Content-Length.
            let mut limit = buffer.len().min(pack.size - bytes.len() + 1);
            let rate = control.speed_limit(credit.stats.limit);
            if rate != 0 {
                limit = limit.min(usize::try_from(rate).unwrap_or(usize::MAX));
            }
            let count = response
                .read(&mut buffer[..limit])
                .map_err(|e| TransferError::Network(e.to_string()))?;
            if count == 0 {
                break;
            }
            credit.stats.received(count as u64, control)?;
            if count > pack.size - bytes.len() {
                return Err(TransferError::Protocol("oversized small pack".into()));
            }
            bytes.extend_from_slice(&buffer[..count]);
            credit.set(bytes.len() as u64);
            credit
                .stats
                .emit(TransferStage::Downloading, first, event, false);
        }
        if bytes.len() != pack.size || hex(&Sha256::digest(&bytes)) != pack.sha256 {
            return Err(TransferError::Integrity(first.clone()));
        }
        // Never write any part of a bundle whose members disagree with the
        // ordinary per-file manifest, even if the bundle hash itself matches.
        for (file, payload) in pack.members(plan, &bytes) {
            control.checkpoint()?;
            let mut hashes = FileHashes::new(file);
            hashes.update(payload);
            if hashes.matches(file).is_none() {
                return Err(TransferError::Integrity(file.path.clone()));
            }
        }
        Ok(bytes)
    }
}

#[cfg(test)]
#[path = "small_packs_tests.rs"]
mod tests;
