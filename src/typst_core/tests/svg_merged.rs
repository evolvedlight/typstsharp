use std::ffi::{c_char, CString};

use typst_core::{Compiler, compile, create_compiler, free_compile_result, free_compiler};

/// Two pages of different widths, so the merged image's size shows both how the pages are
/// stacked and that the widest one sets the width.
const TWO_PAGES: &[u8] = b"#set page(width: 100pt, height: 50pt, margin: 0pt)
First
#set page(width: 80pt, height: 30pt)
Second";

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

/// Compiles `source` to `format` and returns either every output buffer as a string or the
/// error message.
fn compile_to(source: &[u8], format: &str, merged_gap: f32) -> Result<Vec<String>, String> {
    let compiler = compiler_for(source);
    let format = CString::new(format).unwrap();

    let result = unsafe { compile(compiler, format.as_ptr(), 96.0, merged_gap, std::ptr::null()) };

    let outcome = unsafe {
        if result.error_ptr.is_null() {
            let buffers = std::slice::from_raw_parts(result.buffers, result.buffers_len);
            Ok(buffers
                .iter()
                .map(|b| {
                    String::from_utf8(std::slice::from_raw_parts(b.ptr, b.len).to_vec()).unwrap()
                })
                .collect())
        } else {
            let slice = std::slice::from_raw_parts(result.error_ptr, result.error_len);
            Err(String::from_utf8_lossy(slice).into_owned())
        }
    };

    unsafe {
        free_compile_result(result);
        free_compiler(compiler);
    }

    outcome
}

#[test]
fn every_page_is_merged_into_one_svg() {
    let svgs = compile_to(TWO_PAGES, "svg-merged", 0.0).unwrap();

    assert_eq!(svgs.len(), 1);
    assert_eq!(svgs[0].matches("<svg").count(), 1, "{}", svgs[0]);
    assert!(svgs[0].contains(r#"viewBox="0 0 100 80""#), "{}", svgs[0]);
}

#[test]
fn the_gap_is_added_between_pages() {
    let svgs = compile_to(TWO_PAGES, "svg-merged", 12.5).unwrap();

    assert!(svgs[0].contains(r#"viewBox="0 0 100 92.5""#), "{}", svgs[0]);
}

#[test]
fn plain_svg_still_produces_one_image_per_page() {
    let svgs = compile_to(TWO_PAGES, "svg", 0.0).unwrap();

    assert_eq!(svgs.len(), 2);
}

#[test]
fn a_negative_or_non_finite_gap_is_rejected() {
    for gap in [-1.0, f32::NAN, f32::INFINITY] {
        let error = compile_to(TWO_PAGES, "svg-merged", gap).unwrap_err();
        assert!(error.contains("non-negative"), "gap {gap}: {error}");
    }
}
