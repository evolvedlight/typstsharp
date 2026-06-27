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
    ) -> StrResult<Self> {
        let mut fonts = typst_kit::fonts::FontStore::new();

        if include_system_fonts {
            fonts.extend(typst_kit::fonts::system());
        }

        fonts.extend(typst_kit::fonts::embedded());

        for path in font_paths {
            fonts.extend(typst_kit::fonts::scan(path));
        }

        // Resolve the main file path relative to the root
        // If the input path is absolute, try to make it relative to the root.
        // If it's already relative, assume it's relative to the root.
        let main_id = if let Some(path) = input_path {
            let relative_path = if path.is_absolute() {
                path.strip_prefix(&root).map_err(|_| {
                    eco_format!("input file must be contained in the project root")
                })?
            } else {
                &path
            };
            let relative_str = relative_path.to_str().ok_or_else(|| {
                eco_format!("input file path must be valid UTF-8")
            })?;
            RootedPath::new(VirtualRoot::Project, VirtualPath::new(relative_str).unwrap()).intern()
        } else {
            FileId::unique(RootedPath::new(VirtualRoot::Project, VirtualPath::new("<main>").unwrap()))
        };

        let mut slots = HashMap::new();
        if let Some(content) = input_content {
            let mut main_slot = FileSlot::new(main_id);
            main_slot.source.init(Source::new(main_id, content));
            slots.insert(main_id, main_slot);
        }

        let book = fonts.book().clone();

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
                package_path.map(FsPackages::new).or_else(FsPackages::system_data),
                FsPackages::system_cache(),
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

    /// Resets the cached date/time between compilations.
    pub fn reset_time(&mut self) {
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
}

fn system_path(
    root: &Path,
    id: FileId,
    packages: &SystemPackages,
) -> FileResult<PathBuf> {
    match id.root() {
        VirtualRoot::Project => Ok(id.vpath().realize(root)),
        VirtualRoot::Package(spec) => {
            let package_root = packages.obtain(spec)?;
            Ok(package_root.resolve(id.vpath()))
        }
    }
}

struct SlotCell<T> {
    data: Option<FileResult<T>>,
    fingerprint: u128,
    accessed: bool,
}

impl<T: Clone> SlotCell<T> {
    fn new() -> Self {
        Self {
            data: None,
            fingerprint: 0,
            accessed: false,
        }
    }

    fn init(&mut self, data: T) {
        self.data = Some(Ok(data));
        self.accessed = true;
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
