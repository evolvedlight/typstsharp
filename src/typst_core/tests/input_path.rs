//! Tests for how an on-disk input path is turned into Typst's virtual main file.
//!
//! A virtual path only accepts forward slashes and may not leave the project root.
//! Both constraints are properties of the path the caller supplies, so they have to
//! be reported as errors rather than taking the process down.

use std::ffi::{c_char, CString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use typst_core::{Compiler, compile, create_compiler, free_compile_result, free_compiler};

/// A throwaway directory holding a template, removed when the test ends.
struct Project {
    root: PathBuf,
}

impl Project {
    /// Creates a project root containing `templates/letter.typ`.
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
        std::fs::create_dir_all(root.join("templates")).unwrap();
        std::fs::write(root.join("templates").join("letter.typ"), "= Dear customer").unwrap();
        Self { root }
    }
}

impl Drop for Project {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// Creates a compiler over a file, the way `TypstCompiler.FromFile` does.
fn compiler_for_file(root: &Path, input_path: &str) -> *mut Compiler {
    let root = CString::new(root.to_str().unwrap()).unwrap();
    let input_path = CString::new(input_path).unwrap();
    let sys_inputs = CString::new("{}").unwrap();

    create_compiler(
        root.as_ptr(),
        input_path.as_ptr(),
        std::ptr::null(),
        0,
        std::ptr::null::<*const c_char>(),
        0,
        std::ptr::null(),
        sys_inputs.as_ptr(),
        true,
        true,
    )
}

/// Compiles to a PDF and returns its length, failing the test on a compiler error.
fn compile_to_pdf_len(compiler: *mut Compiler) -> usize {
    let result = compile(compiler, std::ptr::null(), 96.0, std::ptr::null());

    assert!(
        result.error_ptr.is_null(),
        "compilation failed: {}",
        unsafe {
            String::from_utf8_lossy(std::slice::from_raw_parts(
                result.error_ptr,
                result.error_len,
            ))
            .into_owned()
        }
    );
    assert_eq!(result.buffers_len, 1, "expected exactly one PDF buffer");

    let len = unsafe { (*result.buffers).len };
    free_compile_result(result);
    len
}

/// A template in a subfolder is addressed with the platform's own separator. On
/// Windows that is a backslash, which a virtual path rejects outright, so the path
/// has to be resolved through `Path` rather than handed over as a string.
#[test]
fn nested_relative_input_path_is_compiled() {
    let project = Project::new("nested-relative");
    let input_path = Path::new("templates")
        .join("letter.typ")
        .to_str()
        .unwrap()
        .to_owned();

    let compiler = compiler_for_file(&project.root, &input_path);
    assert!(
        !compiler.is_null(),
        "`{input_path}` was rejected as an input path"
    );

    assert!(compile_to_pdf_len(compiler) > 0, "produced an empty PDF");
    free_compiler(compiler);
}

/// The separator is spelled out rather than taken from `Path`, so this stays a
/// regression test even if the platform-independent one above is ever simplified to a
/// forward-slash literal, which the old code already accepted.
#[cfg(windows)]
#[test]
fn windows_separator_in_the_input_path_is_compiled() {
    let project = Project::new("windows-separator");

    let compiler = compiler_for_file(&project.root, "templates\\letter.typ");
    assert!(
        !compiler.is_null(),
        "`templates\\letter.typ` was rejected as an input path"
    );

    assert!(compile_to_pdf_len(compiler) > 0, "produced an empty PDF");
    free_compiler(compiler);
}

/// A path that steps out of a subfolder and back in never leaves the root, so it
/// resolves rather than being refused. This is the boundary of the escape check.
#[test]
fn input_path_leaving_and_reentering_the_root_is_compiled() {
    let project = Project::new("reentering");
    let input_path = Path::new("templates")
        .join("..")
        .join("templates")
        .join("letter.typ")
        .to_str()
        .unwrap()
        .to_owned();

    let compiler = compiler_for_file(&project.root, &input_path);
    assert!(!compiler.is_null(), "`{input_path}` was rejected");

    assert!(compile_to_pdf_len(compiler) > 0, "produced an empty PDF");
    free_compiler(compiler);
}

/// The same file named absolutely resolves to the same document.
#[test]
fn absolute_input_path_inside_the_root_is_compiled() {
    let project = Project::new("absolute-inside");
    let input_path = project.root.join("templates").join("letter.typ");

    let compiler = compiler_for_file(&project.root, input_path.to_str().unwrap());
    assert!(!compiler.is_null(), "absolute input path was rejected");

    assert!(compile_to_pdf_len(compiler) > 0, "produced an empty PDF");
    free_compiler(compiler);
}

/// A relative path that climbs out of the root has to be refused. Reaching outside
/// the root is what the root is for, and the refusal must be an error result rather
/// than a panic: this runs inside an `extern "C"` function, where an unwind aborts
/// the host process.
#[test]
fn relative_input_path_escaping_the_root_is_refused() {
    let project = Project::new("relative-escape");
    let input_path = Path::new("..")
        .join("outside.typ")
        .to_str()
        .unwrap()
        .to_owned();

    let compiler = compiler_for_file(&project.root, &input_path);
    assert!(
        compiler.is_null(),
        "`{input_path}` escapes the root and must be refused"
    );
}

/// An absolute path outside the root is refused for the same reason.
#[test]
fn absolute_input_path_outside_the_root_is_refused() {
    let project = Project::new("absolute-outside");
    let outside = std::env::temp_dir().join("typst_core-outside.typ");

    let compiler = compiler_for_file(&project.root, outside.to_str().unwrap());
    assert!(compiler.is_null(), "path outside the root must be refused");
}
