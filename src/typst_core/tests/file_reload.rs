//! Tests that a compiler reused across compilations sees the current content of
//! the files it reads.
//!
//! Keeping one compiler alive is the recommended way to benefit from the incremental
//! cache, so the files it reads must not be pinned to whatever they contained during
//! the first compilation. Documents that were handed over in memory have no file
//! behind them and have to survive the reset instead.

use std::ffi::{c_char, CString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use typst_core::{Compiler, compile, create_compiler, free_compile_result, free_compiler};

/// A throwaway project directory, removed when the test ends.
struct Project {
    root: PathBuf,
}

impl Project {
    fn new(name: &str) -> Self {
        // A counter keeps parallel tests apart without pulling in a temp-file crate.
        static COUNTER: AtomicUsize = AtomicUsize::new(0);
        let unique = COUNTER.fetch_add(1, Ordering::Relaxed);

        let root = std::env::temp_dir().join(format!(
            "typst_core-{}-{}-{}",
            name,
            std::process::id(),
            unique
        ));
        std::fs::create_dir_all(&root).unwrap();
        Self { root }
    }

    /// Writes a file into the project, replacing whatever was there before.
    fn write(&self, name: &str, content: &str) {
        let path = self.root.join(name);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, content).unwrap();
    }

    /// Removes a file from the project.
    fn remove(&self, name: &str) {
        std::fs::remove_file(self.root.join(name)).unwrap();
    }
}

impl Drop for Project {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// Creates a compiler over a project root, with the document taken from a file, from
/// memory, or from both.
fn compiler_for(root: &Path, input_path: Option<&str>, source: Option<&str>) -> *mut Compiler {
    let root = CString::new(root.to_str().unwrap()).unwrap();
    let input_path = input_path.map(|path| CString::new(path).unwrap());
    let sys_inputs = CString::new("{}").unwrap();

    let compiler = unsafe {
        create_compiler(
            root.as_ptr(),
            input_path.as_ref().map_or(std::ptr::null(), |path| path.as_ptr()),
            source.map_or(std::ptr::null(), |source| source.as_ptr()),
            source.map_or(0, |source| source.len()),
            std::ptr::null::<*const c_char>(),
            0,
            std::ptr::null(),
            sys_inputs.as_ptr(),
            true,
            true,
        )
    };
    assert!(!compiler.is_null(), "failed to create compiler");
    compiler
}

/// Creates a compiler over a file, the way `TypstCompiler.FromFile` does.
fn compiler_for_file(root: &Path, input_path: &str) -> *mut Compiler {
    compiler_for(root, Some(input_path), None)
}

/// Creates a compiler over an in-memory document, the way `TypstCompiler.FromSource` does.
fn compiler_for_source(root: &Path, source: &str) -> *mut Compiler {
    compiler_for(root, None, Some(source))
}

/// One compilation to a PDF: the error message on failure, the PDF length on success.
fn compile_to_pdf(compiler: *mut Compiler) -> Result<usize, String> {
    let result = unsafe { compile(compiler, std::ptr::null(), 96.0, std::ptr::null()) };

    let outcome = if result.error_ptr.is_null() {
        assert_eq!(result.buffers_len, 1, "expected exactly one PDF buffer");
        Ok(unsafe { (*result.buffers).len })
    } else {
        Err(unsafe {
            String::from_utf8_lossy(std::slice::from_raw_parts(
                result.error_ptr,
                result.error_len,
            ))
            .into_owned()
        })
    };

    unsafe { free_compile_result(result) };
    outcome
}

/// Compiles and fails the test unless the document is rejected.
fn compile_expecting_error(compiler: *mut Compiler) -> String {
    match compile_to_pdf(compiler) {
        Ok(_) => panic!("an invalid document compiled without an error"),
        Err(error) => error,
    }
}

/// Compiles and fails the test unless the document is accepted.
fn compile_expecting_success(compiler: *mut Compiler) -> usize {
    match compile_to_pdf(compiler) {
        Ok(len) => len,
        Err(error) => panic!("compilation failed: {error}"),
    }
}

/// Rewriting the main file has to change what the next compilation sees. The two
/// versions fail on differently named variables, so the error message says which of
/// them was compiled rather than only that the file was read again.
#[test]
fn a_rewritten_main_file_is_compiled_again() {
    let project = Project::new("rewritten-main");
    project.write("main.typ", "#before_the_change");
    let compiler = compiler_for_file(&project.root, "main.typ");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("before_the_change"),
        "unexpected compiler error: {error}"
    );

    project.write("main.typ", "#after_the_change");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("after_the_change"),
        "the compiler kept serving the content it read first: {error}"
    );

    unsafe { free_compiler(compiler) };
}

/// Imports are read through the same slots as the main file, so a template that is
/// split across files has to be picked up in the same way.
#[test]
fn a_rewritten_import_is_compiled_again() {
    let project = Project::new("rewritten-import");
    project.write(
        "letter.typ",
        "#import \"salutation.typ\": salutation\n#salutation",
    );
    project.write("salutation.typ", "#let salutation = [Dear customer]");
    let compiler = compiler_for_file(&project.root, "letter.typ");

    assert!(
        compile_expecting_success(compiler) > 0,
        "produced an empty PDF"
    );

    project.write("salutation.typ", "#let salutation = after_the_change");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("after_the_change"),
        "the compiler kept serving the import it read first: {error}"
    );

    unsafe { free_compiler(compiler) };
}

/// A failed read is cached like a successful one, so fixing the file on disk has to
/// clear the error as well. Without this a compiler that outlived one broken deploy
/// would keep reporting the same error forever.
#[test]
fn a_file_repaired_on_disk_compiles_again() {
    let project = Project::new("repaired-file");
    project.write("report.typ", "#not_yet_defined");
    let compiler = compiler_for_file(&project.root, "report.typ");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("not_yet_defined"),
        "unexpected compiler error: {error}"
    );

    project.write("report.typ", "= A working report");

    assert!(
        compile_expecting_success(compiler) > 0,
        "produced an empty PDF"
    );

    unsafe { free_compiler(compiler) };
}

/// A document handed over as a string is not backed by a file. Re-reading its slot
/// would look for `<main>` on disk and fail, so the reset has to leave it alone.
#[test]
fn an_in_memory_document_survives_a_second_compilation() {
    let project = Project::new("in-memory");
    let compiler = compiler_for_source(&project.root, "= Hello from memory");

    assert!(
        compile_expecting_success(compiler) > 0,
        "produced an empty PDF"
    );
    assert!(
        compile_expecting_success(compiler) > 0,
        "the in-memory document was lost between compilations"
    );

    unsafe { free_compiler(compiler) };
}

/// The exemption belongs to the one slot that was handed a value, not to every file a
/// document in memory reaches. Its imports still live on disk and still have to be
/// read again.
#[test]
fn an_in_memory_document_still_re_reads_its_imports() {
    let project = Project::new("in-memory-import");
    project.write("salutation.typ", "#let salutation = [Dear customer]");
    let compiler = compiler_for_source(
        &project.root,
        "#import \"salutation.typ\": salutation\n#salutation",
    );

    assert!(
        compile_expecting_success(compiler) > 0,
        "produced an empty PDF"
    );

    project.write("salutation.typ", "#let salutation = after_the_change");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("after_the_change"),
        "the compiler kept serving the import it read first: {error}"
    );

    unsafe { free_compiler(compiler) };
}

/// A file reached through `read` is held in a second slot, decoded as bytes rather
/// than as Typst source. That is the slot behind `#image`, `#json` and `#csv`, so it
/// has to follow the file just as the source slot does.
#[test]
fn a_rewritten_data_file_is_read_again() {
    let project = Project::new("rewritten-data");
    project.write("report.typ", "#panic(read(\"data.txt\"))");
    project.write("data.txt", "before_the_change");
    let compiler = compiler_for_file(&project.root, "report.typ");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("before_the_change"),
        "unexpected compiler error: {error}"
    );

    project.write("data.txt", "after_the_change");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("after_the_change"),
        "the compiler kept serving the data it read first: {error}"
    );

    unsafe { free_compiler(compiler) };
}

/// A template that disappears has to be reported rather than rendered from memory. A
/// compiler that answered from its first read would hand out a document for a file
/// that is no longer deployed.
#[test]
fn a_deleted_file_is_reported_as_missing() {
    let project = Project::new("deleted-file");
    project.write("letter.typ", "= Dear customer");
    let compiler = compiler_for_file(&project.root, "letter.typ");

    assert!(
        compile_expecting_success(compiler) > 0,
        "produced an empty PDF"
    );

    project.remove("letter.typ");

    let error = compile_expecting_error(compiler);
    assert!(
        error.contains("file not found"),
        "unexpected compiler error: {error}"
    );

    unsafe { free_compiler(compiler) };
}

/// Handing over both a path and a source is not something the managed wrapper does,
/// but the boundary accepts it. The source wins, for every compilation and not only
/// the first: it occupies the slot the file would otherwise be read into.
#[test]
fn an_in_memory_source_shadows_the_file_it_names() {
    let project = Project::new("shadowed-file");
    project.write("letter.typ", "#on_disk");
    let compiler = compiler_for(&project.root, Some("letter.typ"), Some("= In memory"));

    assert!(
        compile_expecting_success(compiler) > 0,
        "the file on disk was compiled instead of the source in memory"
    );

    project.write("letter.typ", "#still_on_disk");

    assert!(
        compile_expecting_success(compiler) > 0,
        "the file on disk was compiled instead of the source in memory"
    );

    unsafe { free_compiler(compiler) };
}
