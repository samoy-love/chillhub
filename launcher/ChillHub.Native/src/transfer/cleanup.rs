//! Cleanup consent names individual paths, never future contents of a directory.
use std::collections::HashSet;

pub(super) fn is_approved(planned: &[String], approved: &[String]) -> bool {
    let approved: HashSet<_> = approved.iter().map(|path| path.to_lowercase()).collect();
    planned
        .iter()
        .all(|path| approved.contains(&path.to_lowercase()))
}

#[cfg(test)]
#[path = "cleanup_tests.rs"]
mod tests;
