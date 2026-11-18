use super::*;
use flate2::{Compression, write::GzEncoder};
use std::io::Write;

fn compress(bytes: &[u8]) -> Vec<u8> {
    let mut encoder = GzEncoder::new(Vec::new(), Compression::fast());
    encoder.write_all(bytes).unwrap();
    encoder.finish().unwrap()
}

#[test]
fn bounded_manifest_reads_at_most_one_byte_past_the_limit() {
    let mut source = Cursor::new(b"0123456789");
    let limit = 5;
    let result = read_bounded(&mut source, limit, TransferError::Io);
    assert!(matches!(result, Err(TransferError::InvalidManifest(_))));
    assert_eq!(source.position(), limit + 1);
    assert_eq!(
        read_bounded(&b"01234"[..], limit, TransferError::Io).unwrap(),
        b"01234"
    );
}

#[test]
fn gzip_manifest_requires_a_valid_complete_representation() {
    let json = b"{}";
    let encoded = compress(json);
    assert_eq!(inflate(encoded.clone(), 2).unwrap(), json);

    let mut bad_crc = encoded.clone();
    let crc_start = bad_crc.len() - 8;
    bad_crc[crc_start] ^= 1;
    let mut truncated = encoded.clone();
    truncated.pop();
    let mut trailing = encoded;
    trailing.extend_from_slice(b"unexpected bytes");
    for broken in [bad_crc, truncated, trailing] {
        assert!(matches!(
            inflate(broken, 128),
            Err(TransferError::InvalidManifest(message)) if message.starts_with("invalid gzip manifest")
        ));
    }
}

#[test]
fn gzip_manifest_limit_applies_to_the_inflated_bytes() {
    let encoded = compress(&[b' '; 1024]);
    assert!(encoded.len() < 128);
    assert!(matches!(
        inflate(encoded, 128),
        Err(TransferError::InvalidManifest(message)) if message == "manifest too large"
    ));
}
