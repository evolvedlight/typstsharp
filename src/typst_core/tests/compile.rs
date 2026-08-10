use std::ffi::{c_char, CStr, CString};

use typst_core::{compile, create_compiler, free_compile_result, free_compiler};

#[test]
fn invalid_single_line_string_produces_an_error() {
    let root = CString::new(".").unwrap();
    let source = CString::new("This is not a valid document: # what?").unwrap();
    let sys_inputs = CString::new("{}").unwrap();

    let compiler = create_compiler(
        root.as_ptr(),
        std::ptr::null(),
        source.as_ptr(),
        std::ptr::null::<*const c_char>(),
        0,
        std::ptr::null(),
        sys_inputs.as_ptr(),
        true,
    );
    assert!(!compiler.is_null(), "failed to create compiler");

    let result = compile(compiler, std::ptr::null(), 96.0, std::ptr::null());
    assert!(!result.error.is_null(), "invalid document compiled without an error");

    let error = unsafe { CStr::from_ptr(result.error).to_string_lossy().into_owned() };
    dbg!(&error);

    free_compile_result(result);
    free_compiler(compiler);

    assert!(
        error.contains("expected expression"),
        "unexpected compiler error: {error}"
    );
}

#[test]
fn invalid_multi_line_string_produces_multiple_errors() {
    let raw_source = r##"This is not a valid document: # what?

    As a test - this should be able to have a second error, as you can't use dollars here: $50"##;

    let root = CString::new(".").unwrap();
    let source = CString::new(raw_source).unwrap();
    let sys_inputs = CString::new("{}").unwrap();

    let compiler = create_compiler(
        root.as_ptr(),
        std::ptr::null(),
        source.as_ptr(),
        std::ptr::null::<*const c_char>(),
        0,
        std::ptr::null(),
        sys_inputs.as_ptr(),
        true,
    );
    assert!(!compiler.is_null(), "failed to create compiler");

    let result = compile(compiler, std::ptr::null(), 96.0, std::ptr::null());
    assert!(!result.error.is_null(), "invalid document compiled without an error");

    let error = unsafe { CStr::from_ptr(result.error).to_string_lossy().into_owned() };
    dbg!(&error);

    free_compile_result(result);
    free_compiler(compiler);

    assert!(
        error.contains("expected expression"),
        "unexpected compiler error: {error}"
    );

    assert!(
        error.contains("unclosed delimiter"),
        "unexpected compiler error: {error}"
    );
}
