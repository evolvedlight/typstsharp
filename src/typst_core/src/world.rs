use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use ecow::eco_format;
use typst::diag::{FileError, FileResult, StrResult};
use typst::foundations::{Bytes, Datetime, Duration};
use typst::syntax::{FileId, RootedPath, Source, VirtualPath, VirtualRoot};
use typst::text::{Font, FontBook};
use typst::utils::LazyHash;
use typst::{Library, LibraryExt, World};
use typst_kit::packages::{FsPackages, SystemPackages, UniversePackages};

/// A world that provides access to the operating system.
pub struct SystemWorld {
    /// The root relative to which absolute paths are resolved.
    root: PathBuf,
    /// The input path.
    main: FileId,
    /// Typst's standard library.
    library: LazyHash<Library>,
    /// Metadata about discovered fonts.
    book: LazyHash<FontBook>,
    /// Locations of and storage for lazily loaded fonts.
    fonts: Arc<typst_kit::fonts::FontStore>,
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
        &self.book
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
        let mut fonts = typst_kit::fonts::FontStore::new();

        if include_system_fonts {
            fonts.extend(typst_kit::fonts::system());
        }

        fonts.extend(typst_kit::fonts::embedded());

        for path in font_paths {
            fonts.extend(typst_kit::fonts::scan(path));
        }

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

        let book = fonts.book().clone();

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
            book,
            fonts: Arc::new(fonts),
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
    /// the next access reads the file from disk again. A compiler is meant to be kept
    /// alive across compilations for its incremental cache, and one that held on to
    /// the content each file had when it was first read would go on rendering a
    /// template that has since been rewritten, and give no sign of it.
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

struct FileSlot {
    id: FileId,
    source: SlotCell<Source>,
    file: SlotCell<Bytes>,
}

impl FileSlot {
    fn new(id: FileId) -> Self {
        Self {
            id,
            file: SlotCell::new(),
            source: SlotCell::new(),
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
            |data, _| Ok(Bytes::new(data)),
        )
    }

    /// Sends both views of the file back to disk for the next compilation.
    ///
    /// Package files are included. A package addressed with a fixed version cannot
    /// legitimately change under it, but a deployment that vendors its templates as
    /// local packages redeploys them exactly the way it redeploys a bare `.typ`, and
    /// that is the case this exists for. A package that failed to resolve is looked up
    /// again for the same reason: one that is vendored afterwards starts working,
    /// rather than staying broken for the life of the compiler.
    fn reset(&mut self) {
        self.source.reset();
        self.file.reset();
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
    /// The cached value and its fingerprint are kept either way: if the file turns
    /// out to be unchanged, the value is handed out again instead of being decoded a
    /// second time, which is what keeps recompiling an unchanged document cheap.
    fn reset(&mut self) {
        if !self.in_memory {
            self.accessed = false;
        }
    }

    fn get_or_init(
        &mut self,
        path: impl FnOnce() -> FileResult<PathBuf>,
        f: impl FnOnce(Vec<u8>, Option<T>) -> FileResult<T>,
    ) -> FileResult<T> {
        if std::mem::replace(&mut self.accessed, true) {
            if let Some(data) = &self.data {
                return data.clone();
            }
        }

        let result = path().and_then(|p| read(&p));
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

fn read(path: &Path) -> FileResult<Vec<u8>> {
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

    /// A cell holds its value until it is reset, and then reports whatever the file
    /// says. Decoding is the expensive half of a read, so it has to happen only when
    /// the content has actually changed: the fingerprint is what tells the two apart.
    #[test]
    fn a_reset_cell_re_reads_the_file_but_decodes_only_what_changed() {
        let dir = TempDir::new("re-read");
        let path = dir.write("note.txt", "first");

        let decodes = Cell::new(0usize);
        let decode = |data: Vec<u8>, _: Option<String>| {
            decodes.set(decodes.get() + 1);
            Ok(String::from_utf8(data).unwrap())
        };
        let mut cell = SlotCell::<String>::new();

        assert_eq!(cell.get_or_init(|| Ok(path.clone()), decode).unwrap(), "first");
        assert_eq!(decodes.get(), 1);

        // Within one compilation the file is read once, however often it is asked for.
        std::fs::write(&path, "second").unwrap();
        assert_eq!(cell.get_or_init(|| Ok(path.clone()), decode).unwrap(), "first");
        assert_eq!(decodes.get(), 1);

        cell.reset();
        assert_eq!(cell.get_or_init(|| Ok(path.clone()), decode).unwrap(), "second");
        assert_eq!(decodes.get(), 2);

        cell.reset();
        assert_eq!(cell.get_or_init(|| Ok(path.clone()), decode).unwrap(), "second");
        assert_eq!(decodes.get(), 2, "an unchanged file was decoded a second time");
    }

    /// A failed read is cached like a successful one, so a file that appears later has
    /// to be picked up rather than reported missing forever.
    #[test]
    fn a_reset_cell_picks_up_a_file_that_did_not_exist_yet() {
        let dir = TempDir::new("appearing");
        let path = dir.path.join("late.txt");

        let decode = |data: Vec<u8>, _: Option<String>| Ok(String::from_utf8(data).unwrap());
        let mut cell = SlotCell::<String>::new();

        assert!(cell.get_or_init(|| Ok(path.clone()), decode).is_err());

        std::fs::write(&path, "here now").unwrap();
        cell.reset();

        assert_eq!(cell.get_or_init(|| Ok(path.clone()), decode).unwrap(), "here now");
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
            |_, _| unreachable!("an in-memory cell must not be decoded"),
        );
        assert_eq!(value.unwrap(), "handed over");
    }
}
