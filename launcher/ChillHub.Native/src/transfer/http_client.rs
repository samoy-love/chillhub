use reqwest::{blocking::Client, redirect::Policy};
use std::time::Duration;

#[derive(Clone, Copy)]
pub(super) enum Redirects {
    SameOrigin,
    Disabled,
}

impl Redirects {
    pub(super) fn client(self) -> Result<Client, reqwest::Error> {
        let policy = match self {
            Self::Disabled => Policy::none(),
            Self::SameOrigin => Policy::custom(|attempt| {
                let target = attempt.url();
                let allowed = attempt.previous().first().is_some_and(|initial| {
                    initial.origin() == target.origin()
                        && target.username().is_empty()
                        && target.password().is_none()
                });
                if attempt.previous().len() >= 5 || !allowed {
                    attempt.error("Content redirects must stay on the original trusted origin")
                } else {
                    attempt.follow()
                }
            }),
        };
        Client::builder()
            .connect_timeout(Duration::from_secs(10))
            // The blocking client applies the timeout to each read, allowing
            // downloads whose overall duration is much longer than 15 seconds.
            .timeout(Duration::from_secs(15))
            .redirect(policy)
            .no_gzip()
            .no_deflate()
            .no_brotli()
            .no_zstd()
            .user_agent(concat!("ChillHub/", env!("CARGO_PKG_VERSION")))
            .build()
    }
}
