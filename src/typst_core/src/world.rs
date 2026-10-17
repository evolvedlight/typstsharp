use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock, PoisonError};
use std::time::SystemTime;

use ecow::eco_format;
use typst::diag::{FileError, FileResult, StrResult};
use typst::foundations::{Bytes, Datetime, Duration};
use typst::syntax::{FileId, RootedPath, Source, VirtualPath, VirtualRoot};
use typst::text::{Font, FontBook};
use typst::utils::LazyHash;
use typst::{Library, LibraryExt, World};
use typst_kit::fonts::FontStore;
use typst_kit::packages::{FsPackages, SystemPackages, UniversePackages};

/// A world that provides access to the operating system.
pub struct SystemWorld {
    /// The root relative to which absolute paths are resolved.
    root: PathBuf,
    /// The input path.
    main: FileId,
    /// Typst's standard library.
    library: LazyHash<Library>,
    /// Locations of and storage for lazily loaded fonts, shared with every other world
    /// that has the same font configuration.
    fonts: Arc<FontStore>,
    /// Maps file ids to source files and buffers.
    slots: Mutex<HashMap<FileId, FileSlot>>,
    /// Holds information about where packages are stored.
    packages: SystemPackages,
    /// The current datetime if requested. This is stored here to ensure it is
    /// always the same within one compilation. Reset between compilations.
    now: typst_kit::datetime::Time,
}

impl World for SystemWorld {
    fn library(&self) -> &LazyHash<Library> {
        &self.library
    }

    fn book(&self) -> &LazyHash<FontBook> {
        self.fonts.book()
    }

    fn main(&self) -> FileId {
        self.main
    }

    fn source(&self, id: FileId) -> FileResult<Source> {
        self.slot(id, |slot| slot.source(&self.root, &self.packages))
    }

    fn file(&self, id: FileId) -> FileResult<Bytes> {
        self.slot(id, |slot| slot.file(&self.root, &self.packages))
    }

    fn font(&self, index: usize) -> Option<Font> {
        self.fonts.font(index)
    }

    fn today(&self, offset: Option<Duration>) -> Option<Datetime> {
        self.now.today(offset)
    }
}

impl SystemWorld {
    pub fn new(
        root: PathBuf,
        font_paths: &[PathBuf],
        package_path: Option<PathBuf>,
        inputs: typst::foundations::Dict,
        input_path: Option<PathBuf>,
        input_content: Option<String>,
        include_system_fonts: bool,
        include_system_packages: bool,
    ) -> StrResult<Self> {
        let fonts = shared_font_store(include_system_fonts, font_paths);

        // Resolve the main file path relative to the root. A relative input path is
        // taken to be relative to the root, so joining it onto the root first leaves
        // `virtualize` with a single job: translate a real path into a virtual one and
        // check that it stays inside the root.
        //
        // Going through `Path` rather than handing the string to `VirtualPath::new`
        // matters because a virtual path only accepts forward slashes. On Windows the
        // separator in `templates\letter.typ` is an ordinary one, and `Path` splits on
        // it; `VirtualPath::new` would instead reject the whole string.
        //
        // Input (Windows):  root `C:\app`, path `templates\letter.typ`
        // Output:           virtual path `/templates/letter.typ`
        let main_id = if let Some(path) = input_path {
            let absolute = if path.is_absolute() { path } else { root.join(path) };
            let virtual_path = VirtualPath::virtualize(&root, &absolute).map_err(|err| {
                eco_format!("invalid input file path `{}`: {err}", absolute.display())
            })?;
            RootedPath::new(VirtualRoot::Project, virtual_path).intern()
        } else {
            FileId::unique(RootedPath::new(
                VirtualRoot::Project,
                VirtualPath::new("<main>").expect("`<main>` is a valid virtual path"),
            ))
        };

        let mut slots = HashMap::new();
        if let Some(content) = input_content {
            let mut main_slot = FileSlot::new(main_id);
            main_slot.source.init_in_memory(Source::new(main_id, content));
            slots.insert(main_id, main_slot);
        }

        // Packages are looked up in the configured directory first, then in the
        // machine-wide data and cache directories, and are finally downloaded from
        // Typst Universe. Dropping the last two is what a deployment that ships its
        // packages next to the application needs: `SystemPackages::obtain` only
        // reaches the registry through a cache directory, so leaving the cache out
        // keeps resolution on the configured directory and off the network.
        let package_data = match package_path {
            Some(path) => Some(FsPackages::new(path)),
            None if include_system_packages => FsPackages::system_data(),
            None => None,
        };

        let package_cache = if include_system_packages {
            FsPackages::system_cache()
        } else {
            None
        };

        Ok(Self {
            root,
            main: main_id,
            library: LazyHash::new(
                typst::Library::builder()
                    .with_features([typst::Feature::Html].into_iter().collect::<typst::Features>())
                    .with_inputs(inputs)
                    .build(),
            ),
            fonts,
            slots: Mutex::new(slots),
            packages: SystemPackages::from_parts(
                package_data,
                package_cache,
                UniversePackages::new(crate::download::downloader()),
            ),
            now: typst_kit::datetime::Time::system(),
        })
    }

    /// Replace the system inputs used by the library. This rebuilds the
    /// internal `Library` with the provided inputs so that subsequent
    /// compilations see the updated values.
    pub fn set_inputs(&mut self, inputs: typst::foundations::Dict) -> StrResult<()> {
        self.library = LazyHash::new(
            typst::Library::builder()
                .with_features([typst::Feature::Html].into_iter().collect::<typst::Features>())
                .with_inputs(inputs)
                .build(),
        );
        Ok(())
    }

    /// Prepares the world for a new compilation.
    ///
    /// Drops the cached date/time and marks every file slot as not yet accessed, so
    /// the next access checks the file on disk again. A compiler is meant to be kept
    /// alive across compilations for its incremental cache, and one that held on to
    /// the content each file had when it was first read would go on rendering a
    /// template that has since been rewritten, and give no sign of it.
    ///
    /// The check is one stat per file the compilation touches: a file is read again
    /// only when its size or modification time has changed since it was last read.
    ///
    /// Slots holding a document that was handed over in memory keep it: there is no
    /// file behind them to read.
    ///
    /// The lock is taken rather than reached past with `Mutex::get_mut`: `slot` below
    /// inserts into the map and can reallocate it, and the only thing keeping that off
    /// another thread is the caller honouring the rule that a compiler belongs to one
    /// thread.
    pub fn reset(&mut self) {
        for slot in self.slots.lock().unwrap().values_mut() {
            slot.reset();
        }
        self.now.reset();
    }

    fn slot<F, T>(&self, id: FileId, f: F) -> T
    where
        F: FnOnce(&mut FileSlot) -> T,
    {
        let mut map = self.slots.lock().unwrap();
        f(map.entry(id).or_insert_with(|| FileSlot::new(id)))
    }
}

/// Which fonts a world can see: whether the system fonts are included, and the extra
/// directories scanned, in the order given.
type FontConfig = (bool, Vec<PathBuf>);

/// Returns the font store for a font configuration, building it on first use.
///
/// Building one scans every font on the system, which takes tens of milliseconds and is
/// most of what creating a world costs. A store also loads each font it hands out into
/// memory of its own. Sharing one store per configuration across the process pays for
/// the scan once, and keeps a single copy of every font however many worlds use it.
///
/// Stores are kept for the life of the process, also after the last world using one is
/// gone, so a caller that creates a world per compilation scans only the first time.
/// The price is that a font installed after its configuration was first scanned is not
/// seen until the process restarts.
///
/// The lock is held while a store is built, so concurrent first uses scan once rather
/// than racing. A world created during a scan waits for it even if its own store is
/// already built; that happens once per configuration, so the cost is bounded. A panic during
/// the scan leaves the map as it was, since the store is inserted only once complete, so
/// a poisoned lock is recovered rather than failing every later world.
fn shared_font_store(include_system_fonts: bool, font_paths: &[PathBuf]) -> Arc<FontStore> {
    static STORES: OnceLock<Mutex<HashMap<FontConfig, Arc<FontStore>>>> = OnceLock::new();

    let mut stores = STORES
        .get_or_init(Default::default)
        .lock()
        .unwrap_or_else(PoisonError::into_inner);

    stores
        .entry((include_system_fonts, font_paths.to_vec()))
        .or_insert_with(|| {
            let mut fonts = FontStore::new();
            if include_system_fonts {
                fonts.extend(typst_kit::fonts::system());
            }
            fonts.extend(typst_kit::fonts::embedded());
            for path in font_paths {
                fonts.extend(typst_kit::fonts::scan(path));
            }
            Arc::new(fonts)
        })
        .clone()
}

struct FileSlot {
    id: FileId,
    source: SlotCell<Source>,
    file: SlotCell<Bytes>,
    /// The file's stat for the current compilation, shared by both views so that a
    /// file used both as source and as bytes is stat'ed once.
    probe: Probe,
}

impl FileSlot {
    fn new(id: FileId) -> Self {
        Self {
            id,
            file: SlotCell::new(),
            source: SlotCell::new(),
            probe: Probe::default(),
        }
    }

    fn source(
        &mut self,
        project_root: &Path,
        packages: &SystemPackages,
    ) -> FileResult<Source> {
        let id = self.id;
        self.source.get_or_init(
            || system_path(project_root, id, packages),
            &mut self.probe,
            |data, prev| {
                let text = decode_utf8(&data)?;
                if let Some(mut prev) = prev {
                    prev.replace(text);
                    Ok(prev)
                } else {
                    Ok(Source::new(self.id, text.into()))
                }
            },
        )
    }

    fn file(&mut self, project_root: &Path, packages: &SystemPackages) -> FileResult<Bytes> {
        let id = self.id;
        self.file.get_or_init(
            || system_path(project_root, id, packages),
            &mut self.probe,
            |data, _| Ok(Bytes::new(data)),
        )
    }

    /// Sends both views of the file back to disk for the next compilation, where each
    /// is checked against the file's size and modification time and read again only
    /// if those have changed.
    ///
    /// Package files are included, and the stamp check keeps them cheap. A package
    /// addressed with a fixed version cannot legitimately change under it, but a
    /// deployment that vendors its templates as local packages redeploys them exactly
    /// the way it redeploys a bare `.typ`, and that is the case this exists for. A
    /// package that failed to resolve is looked up again for the same reason: one that
    /// is vendored afterwards starts working, rather than staying broken for the life
    /// of the compiler.
    fn reset(&mut self) {
        self.source.reset();
        self.file.reset();
        self.probe = Probe::default();
    }
}

fn system_path(
    root: &Path,
    id: FileId,
    packages: &SystemPackages,
) -> FileResult<PathBuf> {
    match id.root() {
        VirtualRoot::Project => id.vpath().realize(root).map_err(|_| FileError::AccessDenied),
        VirtualRoot::Package(spec) => {
            let package_root = packages.obtain(spec)?;
            package_root.resolve(id.vpath())
        }
    }
}

struct SlotCell<T> {
    data: Option<FileResult<T>>,
    fingerprint: u128,
    /// The file as it was when `data` was read from it, if that is trustworthy enough
    /// to skip reading it again. `None` sends the next access to the file.
    stamp: Option<FileStamp>,
    accessed: bool,
    /// Whether the value was handed over rather than read from a file. Such a cell
    /// has no path behind it, so it is the one thing a reset must not invalidate.
    in_memory: bool,
}

impl<T: Clone> SlotCell<T> {
    fn new() -> Self {
        Self {
            data: None,
            fingerprint: 0,
            stamp: None,
            accessed: false,
            in_memory: false,
        }
    }

    /// Fills the cell with a value that did not come from a file, and pins it there.
    ///
    /// Only a caller that has the content in hand may use this: the cell keeps the
    /// value for the lifetime of the world, because a reset has no file to send it
    /// back to.
    fn init_in_memory(&mut self, data: T) {
        self.data = Some(Ok(data));
        self.accessed = true;
        self.in_memory = true;
    }

    /// Sends the cell back to the file for the next compilation, unless it holds a
    /// value that was handed over rather than read.
    ///
    /// The cached value, its fingerprint and its stamp are kept either way. A file
    /// whose size and modification time still match the stamp is not read at all,
    /// and one that is read but hashes the same is not decoded a second time: that
    /// is what keeps recompiling an unchanged document cheap.
    fn reset(&mut self) {
        if !self.in_memory {
            self.accessed = false;
        }
    }

    /// Hands out the cell's value, going to the file first if the cell was reset.
    ///
    /// `probe` holds the stat of the file for the current compilation. It is shared
    /// with the slot's other view of the same file and has to be cleared between
    /// compilations, or a change on disk goes unnoticed.
    fn get_or_init(
        &mut self,
        path: impl FnOnce() -> FileResult<PathBuf>,
        probe: &mut Probe,
        f: impl FnOnce(Vec<u8>, Option<T>) -> FileResult<T>,
    ) -> FileResult<T> {
        if std::mem::replace(&mut self.accessed, true) {
            if let Some(data) = &self.data {
                return data.clone();
            }
        }

        let (result, stamp) = match path() {
            Ok(path) => {
                let stat = probe.stat(&path);

                // An unchanged stamp stands in for reading and hashing the file: one
                // stat instead of a read, which matters for a large file such as a
                // plugin.
                if let (Some(data), Some(stamp)) = (&self.data, &self.stamp) {
                    if stamp.path == path && stat == Some(stamp.stat) {
                        return data.clone();
                    }
                }

                read(path, stat)
            }
            Err(err) => (Err(err), None),
        };
        self.stamp = stamp;
        let fingerprint = typst::utils::hash128(&result);

        if std::mem::replace(&mut self.fingerprint, fingerprint) == fingerprint {
            if let Some(data) = &self.data {
                return data.clone();
            }
        }

        let prev = self.data.take().and_then(Result::ok);
        let value = result.and_then(|data| f(data, prev));
        self.data = Some(value.clone());

        value
    }
}

/// How long after its last modification a file's stamp is still distrusted.
///
/// A file rewritten within the timestamp granularity of its previous write can end up
/// with the same size and the same modification time, and a stamp taken in that window
/// would hide the second write. Two seconds covers the coarsest common case, FAT and
/// some SMB shares; NTFS and ext4 are far finer. This is git's "racily clean" check.
const RACY_WINDOW: std::time::Duration = std::time::Duration::from_secs(2);

/// What a stat says about a file: enough to tell, without reading it, that it has not
/// changed since an earlier stat.
#[derive(Clone, Copy, PartialEq, Eq)]
struct FileStat {
    len: u64,
    modified: SystemTime,
}

impl FileStat {
    /// Stats a file, or returns `None` if there is nothing a stat could vouch for:
    /// the stat failed, the path is a directory, or the platform has no modification
    /// time.
    fn of(path: &Path) -> Option<Self> {
        let metadata = fs::metadata(path).ok()?;
        if metadata.is_dir() {
            return None;
        }
        Some(Self {
            len: metadata.len(),
            modified: metadata.modified().ok()?,
        })
    }

    /// Whether the file was last modified long enough before `now` that a later
    /// write is guaranteed to move its modification time. A time in the future, from a
    /// skewed clock or a copied file, is never settled.
    fn is_settled(&self, now: SystemTime) -> bool {
        now.duration_since(self.modified).is_ok_and(|age| age >= RACY_WINDOW)
    }
}

/// The stat of a file taken just before it was last read.
///
/// The path is part of the stamp because a package file's path is resolved anew on
/// every access, and a package vendored after the first read moves it.
struct FileStamp {
    path: PathBuf,
    stat: FileStat,
}

/// The stat of a file within one compilation, taken on the first access and reused by
/// every later one.
///
/// Within a compilation each view of a file is pinned to what it saw first anyway, so
/// one stat per file is all the freshness a compilation needs.
#[derive(Default)]
struct Probe {
    taken: Option<(PathBuf, Option<FileStat>)>,
}

impl Probe {
    /// Stats `path`, or returns the stat already taken of it in this compilation.
    fn stat(&mut self, path: &Path) -> Option<FileStat> {
        match &self.taken {
            Some((taken, stat)) if taken == path => *stat,
            _ => {
                let stat = FileStat::of(path);
                self.taken = Some((path.to_path_buf(), stat));
                stat
            }
        }
    }
}

/// Reads a file, and returns with it the stamp that lets the next access skip the
/// read if the file is unchanged. There is no stamp for a failed read, so a file that
/// could not be read is tried again, nor for a file modified too recently to be
/// trusted.
///
/// `stat` was taken before the read, so a write landing between the two leaves a
/// stamp older than the content and the next access reads the file again. Without a
/// stat the file is read the plain way, and a fresh stat says why it could not be.
fn read(path: PathBuf, stat: Option<FileStat>) -> (FileResult<Vec<u8>>, Option<FileStamp>) {
    let Some(stat) = stat else {
        return (read_unstamped(&path), None);
    };

    let result = fs::read(&path).map_err(|e| FileError::from_io(e, &path));
    let settled = result.is_ok() && stat.is_settled(SystemTime::now());
    (result, settled.then_some(FileStamp { path, stat }))
}

fn read_unstamped(path: &Path) -> FileResult<Vec<u8>> {
    let f = |e| FileError::from_io(e, path);
    if fs::metadata(path).map_err(f)?.is_dir() {
        Err(FileError::IsDirectory)
    } else {
        fs::read(path).map_err(f)
    }
}

fn decode_utf8(buf: &[u8]) -> FileResult<&str> {
    Ok(std::str::from_utf8(
        buf.strip_prefix(b"\xef\xbb\xbf").unwrap_or(buf),
    )?)
}

#[cfg(test)]
mod tests {
    use std::cell::Cell;
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::*;

    /// A throwaway directory, removed when the test ends.
    struct TempDir {
        path: PathBuf,
    }

    impl TempDir {
        fn new(name: &str) -> Self {
            // A counter keeps parallel tests apart without pulling in a temp-file crate.
            static COUNTER: AtomicUsize = AtomicUsize::new(0);
            let unique = COUNTER.fetch_add(1, Ordering::Relaxed);

            let path = std::env::temp_dir().join(format!(
                "typst_core-slot-{}-{}-{}",
                name,
                std::process::id(),
                unique
            ));
            std::fs::create_dir_all(&path).unwrap();
            Self { path }
        }

        /// Writes a file into the directory and returns its path.
        fn write(&self, name: &str, content: &str) -> PathBuf {
            let file = self.path.join(name);
            std::fs::write(&file, content).unwrap();
            file
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.path);
        }
    }

    /// Gives a file the modification time a deployed template would have, long past the
    /// racy window, or puts back one that a rewrite moved.
    fn set_modified(path: &Path, time: SystemTime) {
        std::fs::File::options()
            .write(true)
            .open(path)
            .unwrap()
            .set_modified(time)
            .unwrap();
    }

    fn an_hour_ago() -> SystemTime {
        SystemTime::now() - std::time::Duration::from_secs(3600)
    }

    /// Reads a cell through `path` as the only access to the file in a compilation,
    /// counting how often it decodes.
    fn get(
        cell: &mut SlotCell<String>,
        path: &Path,
        decodes: &Cell<usize>,
    ) -> FileResult<String> {
        get_with(cell, path, &mut Probe::default(), decodes)
    }

    /// Reads a cell through `path` with the stat other accesses in the same compilation
    /// share, counting how often it decodes.
    fn get_with(
        cell: &mut SlotCell<String>,
        path: &Path,
        probe: &mut Probe,
        decodes: &Cell<usize>,
    ) -> FileResult<String> {
        cell.get_or_init(
            || Ok(path.to_path_buf()),
            probe,
            |data, _| {
                decodes.set(decodes.get() + 1);
                Ok(String::from_utf8(data).unwrap())
            },
        )
    }

    /// A cell holds its value until it is reset, and then reports whatever the file
    /// says. Decoding is the expensive half of a read, so it has to happen only when
    /// the content has actually changed: the fingerprint is what tells the two apart.
    #[test]
    fn a_reset_cell_re_reads_the_file_but_decodes_only_what_changed() {
        let dir = TempDir::new("re-read");
        let path = dir.write("note.txt", "first");

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");
        assert_eq!(decodes.get(), 1);

        // Within one compilation the file is read once, however often it is asked for.
        std::fs::write(&path, "second").unwrap();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");
        assert_eq!(decodes.get(), 1);

        cell.reset();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "second");
        assert_eq!(decodes.get(), 2);

        cell.reset();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "second");
        assert_eq!(decodes.get(), 2, "an unchanged file was decoded a second time");
    }

    /// A failed read is cached like a successful one, so a file that appears later has
    /// to be picked up rather than reported missing forever.
    #[test]
    fn a_reset_cell_picks_up_a_file_that_did_not_exist_yet() {
        let dir = TempDir::new("appearing");
        let path = dir.path.join("late.txt");

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();

        assert!(get(&mut cell, &path, &decodes).is_err());

        std::fs::write(&path, "here now").unwrap();
        cell.reset();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "here now");
    }

    /// A value that was handed over has no file behind it, so a reset must leave it
    /// alone. Both closures fail the test if the cell goes looking for one.
    #[test]
    fn an_in_memory_cell_is_untouched_by_a_reset() {
        let mut cell = SlotCell::<String>::new();
        cell.init_in_memory("handed over".to_string());

        cell.reset();

        let value = cell.get_or_init(
            || panic!("an in-memory cell must not be resolved to a path"),
            &mut Probe::default(),
            |_, _| unreachable!("an in-memory cell must not be decoded"),
        );
        assert_eq!(value.unwrap(), "handed over");
    }

    /// An unchanged size and modification time stand in for reading the file. The proof
    /// is a rewrite that keeps both: only a cell that skipped the read still has the
    /// old content.
    #[test]
    fn a_reset_cell_skips_reading_a_file_whose_stamp_is_unchanged() {
        let dir = TempDir::new("stamp-unchanged");
        let path = dir.write("note.txt", "first");
        let deployed = an_hour_ago();
        set_modified(&path, deployed);

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");

        std::fs::write(&path, "fresh").unwrap();
        set_modified(&path, deployed);
        cell.reset();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");
        assert_eq!(decodes.get(), 1);
    }

    /// A rewrite that keeps the length but moves the modification time is read.
    #[test]
    fn a_reset_cell_re_reads_a_file_whose_modification_time_changed() {
        let dir = TempDir::new("stamp-mtime");
        let path = dir.write("note.txt", "first");
        let deployed = an_hour_ago();
        set_modified(&path, deployed);

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");

        std::fs::write(&path, "fresh").unwrap();
        set_modified(&path, deployed + std::time::Duration::from_secs(60));
        cell.reset();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "fresh");
    }

    /// A rewrite that keeps the modification time but changes the length is read.
    #[test]
    fn a_reset_cell_re_reads_a_file_whose_length_changed() {
        let dir = TempDir::new("stamp-len");
        let path = dir.write("note.txt", "first");
        let deployed = an_hour_ago();
        set_modified(&path, deployed);

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");

        std::fs::write(&path, "a longer second version").unwrap();
        set_modified(&path, deployed);
        cell.reset();

        assert_eq!(
            get(&mut cell, &path, &decodes).unwrap(),
            "a longer second version"
        );
    }

    /// A file read moments after it was written may be written again within the same
    /// timestamp tick, leaving size and modification time as they were. Its stamp is not
    /// trusted, so the rewrite below, which forges exactly that, is still picked up.
    #[test]
    fn a_file_written_just_now_is_read_again_even_with_an_unchanged_stamp() {
        let dir = TempDir::new("stamp-racy");
        let path = dir.write("note.txt", "first");
        let written = std::fs::metadata(&path).unwrap().modified().unwrap();

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");

        std::fs::write(&path, "fresh").unwrap();
        set_modified(&path, written);
        cell.reset();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "fresh");
    }

    /// A distrusted stamp is judged again on the read it forces, so a file stops being
    /// read on every compilation once it has settled.
    #[test]
    fn a_distrusted_stamp_is_trusted_once_the_file_has_settled() {
        let dir = TempDir::new("stamp-settles");
        let path = dir.write("note.txt", "first");

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");

        // Stands in for the clock moving past the racy window.
        let deployed = an_hour_ago();
        set_modified(&path, deployed);
        cell.reset();
        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");

        std::fs::write(&path, "fresh").unwrap();
        set_modified(&path, deployed);
        cell.reset();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");
        assert_eq!(decodes.get(), 1, "an unchanged file was decoded a second time");
    }

    /// A package file's path is resolved on every access and moves when the package is
    /// vendored somewhere else. Two files with the same size and modification time are
    /// still two files.
    #[test]
    fn a_stamp_does_not_vouch_for_a_file_at_another_path() {
        let dir = TempDir::new("stamp-path");
        let cached = dir.write("cache.typ", "first");
        let vendored = dir.write("vendor.typ", "fresh");
        let deployed = an_hour_ago();
        set_modified(&cached, deployed);
        set_modified(&vendored, deployed);

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        assert_eq!(get(&mut cell, &cached, &decodes).unwrap(), "first");

        cell.reset();

        assert_eq!(get(&mut cell, &vendored, &decodes).unwrap(), "fresh");
    }

    /// Both views of a file share one stat per compilation, so the second is checked
    /// against the stat the first one took. The file is rewritten between the two
    /// accesses with a new length: a second stat would have sent the second view back
    /// to the file.
    #[test]
    fn both_views_of_a_file_share_one_stat_per_compilation() {
        let dir = TempDir::new("stamp-shared");
        let path = dir.write("note.typ", "first");
        set_modified(&path, an_hour_ago());

        let decodes = Cell::new(0);
        let (mut source, mut bytes) = (SlotCell::new(), SlotCell::new());
        assert_eq!(get(&mut source, &path, &decodes).unwrap(), "first");
        assert_eq!(get(&mut bytes, &path, &decodes).unwrap(), "first");

        source.reset();
        bytes.reset();
        let mut probe = Probe::default();
        assert_eq!(get_with(&mut source, &path, &mut probe, &decodes).unwrap(), "first");
        std::fs::write(&path, "a longer second version").unwrap();
        assert_eq!(get_with(&mut bytes, &path, &mut probe, &decodes).unwrap(), "first");

        // The next compilation starts with a fresh stat and sees the rewrite.
        bytes.reset();
        assert_eq!(
            get(&mut bytes, &path, &decodes).unwrap(),
            "a longer second version"
        );
    }

    /// The stat shared within a compilation must not outlive it. The file is dated in
    /// the past so that only the stat, not the racy guard, decides whether the rewrite
    /// is seen.
    #[test]
    fn a_reset_world_sees_a_rewrite_of_a_settled_file() {
        let dir = TempDir::new("world-reset");
        let path = dir.write("main.typ", "first");
        set_modified(&path, an_hour_ago());

        let mut world = SystemWorld::new(
            dir.path.clone(),
            &[],
            None,
            typst::foundations::Dict::new(),
            Some(PathBuf::from("main.typ")),
            None,
            false,
            false,
        )
        .unwrap();
        let main = world.main();
        assert_eq!(world.source(main).unwrap().text(), "first");

        std::fs::write(&path, "a longer second version").unwrap();
        world.reset();

        assert_eq!(
            world.source(main).unwrap().text(),
            "a longer second version"
        );
    }

    /// A file that can be stat'ed but not read keeps no stamp. Otherwise the error would
    /// be served from cache until the file's size or modification time happened to
    /// change, and neither does when the reader that held it releases it.
    #[test]
    fn a_file_that_could_not_be_read_is_read_again() {
        let dir = TempDir::new("stamp-unreadable");
        let path = dir.write("note.txt", "first");
        set_modified(&path, an_hour_ago());

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        {
            let _unreadable = Unreadable::new(&path);
            if std::fs::read(&path).is_ok() {
                // Running as root on Unix, where permissions do not stop a read.
                return;
            }
            assert!(get(&mut cell, &path, &decodes).is_err());
        }
        cell.reset();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "first");
    }

    /// Keeps a file stat-able but unreadable while alive, without touching its size or
    /// modification time: an exclusive handle on Windows, no permissions on Unix.
    struct Unreadable {
        #[cfg(windows)]
        _handle: std::fs::File,
        #[cfg(unix)]
        path: PathBuf,
    }

    impl Unreadable {
        #[cfg(windows)]
        fn new(path: &Path) -> Self {
            use std::os::windows::fs::OpenOptionsExt;
            let handle = std::fs::File::options()
                .read(true)
                .share_mode(0)
                .open(path)
                .unwrap();
            Self { _handle: handle }
        }

        #[cfg(unix)]
        fn new(path: &Path) -> Self {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o000)).unwrap();
            Self {
                path: path.to_path_buf(),
            }
        }
    }

    #[cfg(unix)]
    impl Drop for Unreadable {
        fn drop(&mut self) {
            use std::os::unix::fs::PermissionsExt;
            let _ = std::fs::set_permissions(&self.path, std::fs::Permissions::from_mode(0o644));
        }
    }

    /// A directory has nothing a stamp could vouch for, so it keeps being reported as a
    /// directory, and a file put in its place is picked up.
    #[test]
    fn a_directory_is_reported_until_a_file_replaces_it() {
        let dir = TempDir::new("stamp-dir");
        let path = dir.path.join("entry");
        std::fs::create_dir(&path).unwrap();

        let decodes = Cell::new(0);
        let mut cell = SlotCell::new();
        assert!(matches!(get(&mut cell, &path, &decodes), Err(FileError::IsDirectory)));

        cell.reset();
        assert!(matches!(get(&mut cell, &path, &decodes), Err(FileError::IsDirectory)));

        std::fs::remove_dir(&path).unwrap();
        std::fs::write(&path, "a file now").unwrap();
        cell.reset();

        assert_eq!(get(&mut cell, &path, &decodes).unwrap(), "a file now");
    }

    /// The racy window is closed at its lower bound: a file exactly two seconds old is
    /// settled, one a moment younger is not, and one dated in the future never is.
    #[test]
    fn a_stamp_settles_after_the_racy_window() {
        let modified = an_hour_ago();
        let stamp = FileStat { len: 0, modified };
        let millis = std::time::Duration::from_millis;

        assert!(!stamp.is_settled(modified - millis(1)));
        assert!(!stamp.is_settled(modified));
        assert!(!stamp.is_settled(modified + RACY_WINDOW - millis(1)));
        assert!(stamp.is_settled(modified + RACY_WINDOW));
        assert!(stamp.is_settled(modified + std::time::Duration::from_secs(3600)));
    }

    /// A world over an in-memory document with the given font configuration.
    fn world_with_fonts(font_paths: &[PathBuf], include_system_fonts: bool) -> SystemWorld {
        SystemWorld::new(
            PathBuf::from("."),
            font_paths,
            None,
            typst::foundations::Dict::new(),
            None,
            Some(String::new()),
            include_system_fonts,
            false,
        )
        .unwrap()
    }

    /// Scanning the system fonts is most of what creating a compiler costs, and every
    /// compiler would otherwise read the fonts it uses into memory of its own.
    #[test]
    fn worlds_with_the_same_fonts_share_one_font_store() {
        for include_system_fonts in [false, true] {
            let first = world_with_fonts(&[], include_system_fonts);
            let second = world_with_fonts(&[], include_system_fonts);
            assert!(Arc::ptr_eq(&first.fonts, &second.fonts));
        }
    }

    /// The one-shot helpers on the managed side create a compiler, compile once and free
    /// it. The store has to survive that, or each of them would scan again.
    #[test]
    fn the_font_store_outlives_the_worlds_that_use_it() {
        let first = world_with_fonts(&[], false);
        let store = Arc::as_ptr(&first.fonts);
        drop(first);

        let second = world_with_fonts(&[], false);
        assert_eq!(store, Arc::as_ptr(&second.fonts));
    }

    /// Each font configuration finds a different set of fonts, so none may be served
    /// another's store.
    #[test]
    fn worlds_with_different_fonts_get_different_font_stores() {
        let dir = TempDir::new("font-store");
        let other_dir = TempDir::new("font-store-other");

        let plain = world_with_fonts(&[], false);
        let with_system = world_with_fonts(&[], true);
        let with_dir = world_with_fonts(&[dir.path.clone()], false);
        let with_other_dir = world_with_fonts(&[other_dir.path.clone()], false);

        let stores = [&plain, &with_system, &with_dir, &with_other_dir]
            .map(|world| Arc::as_ptr(&world.fonts));
        for (i, a) in stores.iter().enumerate() {
            for b in &stores[i + 1..] {
                assert_ne!(a, b);
            }
        }
    }
}
