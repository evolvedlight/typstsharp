use std::ffi::{c_char, CString};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Barrier};

use typst_core::{
    Compiler, compile, create_compiler, evict_cache, free_compile_result, free_compiler,
};

/// Creates a compiler over an in-memory document. The source is handed over as
/// raw bytes, so a test can feed content that a C string could not carry.
fn compiler_for(source: &[u8]) -> *mut Compiler {
    let root = CString::new(".").unwrap();
    let sys_inputs = CString::new("{}").unwrap();

    let compiler = unsafe {
        create_compiler(
            root.as_ptr(),
            std::ptr::null(),
            source.as_ptr(),
            source.len(),
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

/// Compiles the document and returns the error message it produced.
fn compile_expecting_error(source: &[u8]) -> String {
    let compiler = compiler_for(source);

    let result = unsafe { compile(compiler, std::ptr::null(), 96.0, 0.0, std::ptr::null()) };
    assert!(
        !result.error_ptr.is_null(),
        "invalid document compiled without an error"
    );

    let error = unsafe {
        let slice = std::slice::from_raw_parts(result.error_ptr, result.error_len);
        String::from_utf8_lossy(slice).into_owned()
    };

    unsafe {
        free_compile_result(result);
        free_compiler(compiler);
    }

    dbg!(&error);
    error
}

#[test]
fn invalid_single_line_string_produces_an_error() {
    let error = compile_expecting_error(b"This is not a valid document: # what?");

    assert!(
        error.contains("expected expression"),
        "unexpected compiler error: {error}"
    );
}

#[test]
fn invalid_multi_line_string_produces_multiple_errors() {
    let raw_source = r##"This is not a valid document: # what?

    As a test - this should be able to have a second error, as you can't use dollars here: $50"##;

    let error = compile_expecting_error(raw_source.as_bytes());

    assert!(
        error.contains("expected expression"),
        "unexpected compiler error: {error}"
    );

    assert!(
        error.contains("unclosed delimiter"),
        "unexpected compiler error: {error}"
    );
}

#[test]
fn source_after_a_nul_byte_is_still_compiled() {
    // Reading the source as a C string stopped at the NUL, so everything after it
    // was dropped and this document compiled clean. The invalid tail is what
    // proves the whole source made it across.
    let error = compile_expecting_error(b"Valid start\0\n\nAnd then: # what?");

    assert!(
        error.contains("expected expression"),
        "source was truncated at the NUL byte: {error}"
    );
}

/// Compiles the document to PDF and returns the bytes, failing the test on an error.
fn compile_to_pdf(compiler: *mut Compiler) -> Vec<u8> {
    let result = unsafe { compile(compiler, std::ptr::null(), 96.0, 0.0, std::ptr::null()) };
    assert!(result.error_ptr.is_null(), "valid document failed to compile");
    assert_eq!(result.buffers_len, 1, "a PDF is exported as one buffer");

    let pdf = unsafe {
        let buffer = &*result.buffers;
        std::slice::from_raw_parts(buffer.ptr, buffer.len).to_vec()
    };

    unsafe { free_compile_result(result) };
    pdf
}

/// A document whose PDF depends on nothing but its source, so two compilations can be compared
/// byte for byte. Without a fixed date, Typst stamps the current one.
fn reproducible(body: &str) -> Vec<u8> {
    format!("#set document(date: none)\n{body}").into_bytes()
}

#[test]
fn a_compiler_still_compiles_after_the_cache_is_cleared() {
    let compiler = compiler_for(&reproducible("= Cached\n\nBody text."));

    let first = compile_to_pdf(compiler);
    assert!(evict_cache(), "clearing the cache failed");
    let second = compile_to_pdf(compiler);

    // The compiler keeps its sources and fonts in its own world, so only the
    // memoised results are gone. Recomputing them gives the same document.
    assert!(second.starts_with(b"%PDF-"), "recompiled output is not a PDF");
    assert!(first == second, "recompiling after the eviction changed the document");

    unsafe { free_compiler(compiler) };
}

#[test]
fn a_result_stays_readable_after_the_cache_is_cleared() {
    let compiler = compiler_for(b"= Held across an eviction");

    let result = unsafe { compile(compiler, std::ptr::null(), 96.0, 0.0, std::ptr::null()) };
    assert!(result.error_ptr.is_null(), "valid document failed to compile");

    // The result owns its buffers, so the cache it came from may go away entirely.
    assert!(evict_cache(), "clearing the cache failed");
    unsafe { free_compiler(compiler) };

    let pdf = unsafe {
        let buffer = &*result.buffers;
        std::slice::from_raw_parts(buffer.ptr, buffer.len)
    };
    assert!(pdf.starts_with(b"%PDF-"), "held output was invalidated by the eviction");

    unsafe { free_compile_result(result) };
}

#[test]
fn the_cache_can_be_cleared_while_other_threads_compile() {
    const THREADS: usize = 4;

    // The main thread joins the barrier too, so it only starts evicting once
    // every thread is about to compile, and keeps evicting until all are done.
    let started = Arc::new(Barrier::new(THREADS + 1));
    let finished = Arc::new(AtomicUsize::new(0));

    let compiling: Vec<_> = (0..THREADS)
        .map(|index| {
            let started = Arc::clone(&started);
            let finished = Arc::clone(&finished);
            std::thread::spawn(move || {
                let source = reproducible(&format!("= Thread {index}\n\n#lorem(200)"));
                let compiler = compiler_for(&source);
                started.wait();

                let first = compile_to_pdf(compiler);
                for _ in 0..10 {
                    assert!(
                        compile_to_pdf(compiler) == first,
                        "an eviction during compilation changed the document"
                    );
                }

                unsafe { free_compiler(compiler) };
                finished.fetch_add(1, Ordering::SeqCst);
            })
        })
        .collect();

    started.wait();
    let mut evictions = 0;
    while finished.load(Ordering::SeqCst) < THREADS {
        assert!(evict_cache(), "clearing the cache failed");
        evictions += 1;
    }

    for thread in compiling {
        thread.join().expect("a compilation failed while the cache was being cleared");
    }

    assert!(evictions > 0, "no eviction overlapped the compilations");
}

#[test]
fn create_compiler_accepts_null_sys_inputs_and_null_font_path_entries() {
    let source = b"= Null pointer resilience";
    let font_paths: [*const c_char; 2] = [std::ptr::null(), std::ptr::null()];

    let compiler = unsafe {
        create_compiler(
            std::ptr::null(),
            std::ptr::null(),
            source.as_ptr(),
            source.len(),
            font_paths.as_ptr(),
            font_paths.len(),
            std::ptr::null(),
            std::ptr::null(),
            true,
            true,
        )
    };
    assert!(!compiler.is_null(), "failed to create compiler with null sys_inputs and font_paths");

    let pdf = compile_to_pdf(compiler);
    assert!(pdf.starts_with(b"%PDF-"));

    unsafe { free_compiler(compiler) };
}