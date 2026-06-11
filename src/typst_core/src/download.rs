use typst_kit::downloader::SystemDownloader;

/// Returns a new downloader.
pub fn downloader() -> SystemDownloader {
    let user_agent = concat!("typstsharp/", env!("CARGO_PKG_VERSION"));
    SystemDownloader::new(user_agent)
}
