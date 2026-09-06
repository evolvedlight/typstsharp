use std::ffi::{c_char, CString};

use typst_core::{Compiler, compile, create_compiler, free_compile_result, free_compiler};

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

    let result = unsafe { compile(compiler, std::ptr::null(), 96.0, std::ptr::null()) };
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
