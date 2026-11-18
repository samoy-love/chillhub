use super::{MAX_MANIFEST, Manifest, Result, TransferControl, TransferError};
use flate2::read::MultiGzDecoder;
use reqwest::{blocking::Response, header::CONTENT_ENCODING};
use std::io::{self, Cursor, Read};

pub(super) fn read(response: Response, control: &TransferControl) -> Result<Manifest> {
    let mut encodings = response.headers().get_all(CONTENT_ENCODING).into_iter();
    let gzip = match (encodings.next(), encodings.next()) {
        (None, None) => false,
        (Some(value), None) if value.as_bytes().eq_ignore_ascii_case(b"identity") => false,
        (Some(value), None) if value.as_bytes().eq_ignore_ascii_case(b"gzip") => true,
        _ => {
            return Err(TransferError::InvalidManifest(
                "unsupported manifest content encoding".into(),
            ));
        }
    };
    if response
        .content_length()
        .is_some_and(|len| len > MAX_MANIFEST)
    {
        return Err(too_large());
    }
    // The HTTP client's automatic decoding is disabled. Bound the received
    // representation independently, including when there is no Content-Length.
    let wire = read_bounded(response, MAX_MANIFEST, |err| {
        TransferError::Network(err.to_string())
    })?;
    control.checkpoint()?;
    let bytes = if gzip {
        inflate(wire, MAX_MANIFEST)?
    } else {
        wire
    };
    control.checkpoint()?;
    serde_json::from_slice(&bytes).map_err(|err| TransferError::InvalidManifest(err.to_string()))
}

fn inflate(wire: Vec<u8>, limit: u64) -> Result<Vec<u8>> {
    // Read through the end of the representation so invalid trailers, truncated
    // members and trailing non-gzip bytes cannot leave accepted JSON behind.
    read_bounded(MultiGzDecoder::new(Cursor::new(wire)), limit, |err| {
        TransferError::InvalidManifest(format!("invalid gzip manifest: {err}"))
    })
}

fn read_bounded(
    input: impl Read,
    limit: u64,
    map_error: impl FnOnce(io::Error) -> TransferError,
) -> Result<Vec<u8>> {
    let mut bytes = Vec::new();
    input
        .take(limit + 1)
        .read_to_end(&mut bytes)
        .map_err(map_error)?;
    if bytes.len() as u64 > limit {
        return Err(too_large());
    }
    Ok(bytes)
}

fn too_large() -> TransferError {
    TransferError::InvalidManifest("manifest too large".into())
}

#[cfg(test)]
#[path = "manifest_body_tests.rs"]
mod tests;
