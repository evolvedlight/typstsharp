use std::fmt::Display;

use typst_kit::download::{DownloadState, Downloader, Progress};

pub struct SilentDownload<T>(pub T);

impl<T: Display> Progress for SilentDownload<T> {
    fn print_start(&mut self) {}

    fn print_progress(&mut self, _state: &DownloadState) {}

    fn print_finish(&mut self, _state: &DownloadState) {}
}

/// Returns a new downloader.
pub fn downloader() -> Downloader {
    let user_agent = concat!("typst-net/", env!("CARGO_PKG_VERSION"));
    Downloader::new(user_agent)
}
