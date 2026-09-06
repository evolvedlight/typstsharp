#![allow(non_camel_case_types)]
use std::ffi::{CStr, c_char};
use std::path::PathBuf;
use std::ptr;

mod compiler;
mod download;
mod world;

use ecow::EcoString;
use typst::diag::{SourceDiagnostic, StrResult, Warned};
use typst::foundations::Dict;
use typst::{World, WorldExt};
use typst_layout::PagedDocument;
use world::SystemWorld;

// This represents the stateful compiler in Rust.
pub struct Compiler(SystemWorld);

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Buffer {
    pub ptr: *mut u8,
    pub len: usize,
}

#[repr(C)]
pub struct Warning {
    pub message_ptr: *mut u8,
    pub message_len: usize,
}

#[repr(C)]
pub struct CompileResult {
    pub buffers: *mut Buffer,
    pub buffers_len: usize,
    pub warnings: *mut Warning,
    pub warnings_len: usize,
    pub error_ptr: *mut u8,
    pub error_len: usize,
}

impl Default for CompileResult {
    fn default() -> Self {
        Self {
            buffers: ptr::null_mut(),
            buffers_len: 0,
            warnings: ptr::null_mut(),
            warnings_len: 0,
            error_ptr: ptr::null_mut(),
            error_len: 0,
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn create_compiler(
    root: *const c_char,
    input_path: *const c_char,
    input_source: *const u8,
    input_source_len: usize,
    font_paths: *const *const c_char,
    font_paths_len: usize,
    package_path: *const c_char,
    sys_inputs: *const c_char,
    ignore_system_fonts: bool,
    ignore_system_packages: bool,
) -> *mut Compiler {
    let root_str = if root.is_null() {
        "."
    } else {
        unsafe { CStr::from_ptr(root).to_str().unwrap_or(".") }
    };
    let root = if root_str.is_empty() {
        PathBuf::from(".")
    } else {
        PathBuf::from(root_str)
    };

    let input_path_buf = if !input_path.is_null() {
        let s = unsafe { CStr::from_ptr(input_path).to_str().unwrap_or("") };
        if s.is_empty() {
            None
        } else {
            Some(PathBuf::from(s))
        }
    } else {
        None
    };

    // The source is taken as an explicit (pointer, length) pair rather than a C
    // string. A Typst document may legitimately contain NUL bytes, and reading it
    // as a C string would silently cut the document off at the first one. A null
    // pointer means "no in-memory source", which is distinct from a zero length,
    // which means an empty document.
    let input_content = if input_source.is_null() {
        None
    } else {
        let bytes = unsafe { std::slice::from_raw_parts(input_source, input_source_len) };
        Some(String::from_utf8_lossy(bytes).into_owned())
    };

    let sys_inputs_str = unsafe { CStr::from_ptr(sys_inputs).to_str().unwrap_or("{}") };

    let font_paths_vec: Vec<PathBuf> = unsafe {
        let slice: &[*const c_char] = if font_paths.is_null() || font_paths_len == 0 {
            &[]
        } else {
            std::slice::from_raw_parts(font_paths, font_paths_len)
        };

        slice
            .iter()
            .map(|&p| PathBuf::from(CStr::from_ptr(p).to_str().unwrap_or("")))
            .collect()
    };

    let package_path_buf = if !package_path.is_null() {
        let s = unsafe { CStr::from_ptr(package_path).to_str().unwrap_or("") };
        if s.is_empty() {
            None
        } else {
            Some(PathBuf::from(s))
        }
    } else {
        None
    };

    let inputs: Dict = serde_json::from_str(sys_inputs_str).unwrap_or_default();

    match SystemWorld::new(
        root,
        &font_paths_vec,
        package_path_buf,
        inputs,
        input_path_buf,
        input_content,
        !ignore_system_fonts,
        !ignore_system_packages,
    ) {
        Ok(world) => Box::into_raw(Box::new(Compiler(world))),
        Err(_) => ptr::null_mut(),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn free_compiler(compiler: *mut Compiler) {
    if !compiler.is_null() {
        unsafe {
            let _ = Box::from_raw(compiler);
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn set_sys_inputs(compiler: *mut Compiler, sys_inputs: *const c_char) -> bool {
    if compiler.is_null() {
        return false;
    }
    let compiler = unsafe { &mut *compiler };

    let sys_inputs_str = if sys_inputs.is_null() {
        "{}"
    } else {
        unsafe { CStr::from_ptr(sys_inputs).to_str().unwrap_or("{}") }
    };

    let inputs: Dict = match serde_json::from_str(sys_inputs_str) {
        Ok(d) => d,
        Err(_) => return false,
    };

    match compiler.0.set_inputs(inputs) {
        Ok(_) => true,
        Err(_) => false,
    }
}

fn compile_inner(
    world: &mut SystemWorld,
    format: &str,
    ppi: f32,
    standards: &[typst_pdf::PdfStandard],
) -> StrResult<(Vec<Vec<u8>>, Vec<SourceDiagnostic>)> {
    world.reset_time();
    let (document, warnings) = match typst::compile::<PagedDocument>(world) {
        Warned { output, warnings } => {
            let doc = output.map_err(|errors| {
                let message = errors
                    .iter()
                    .map(|error| {
                        let location = error.span.id().and_then(|id| {
                            let source = world.source(id).ok()?;
                            let range = world.range(error.span)?;
                            let (line, column) =
                                source.lines().byte_to_line_column(range.start)?;
                            let text = source.text().get(range.clone())?.to_string();

                            Some(format!(
                                "line {}, column {}: `{}`",
                                line + 1,
                                column + 1,
                                text
                            ))
                        });

                        let hints = error
                            .hints
                            .iter()
                            .map(|hint| hint.v.as_str())
                            .collect::<Vec<_>>()
                            .join("; ");

                        match (location, hints.is_empty()) {
                            (Some(location), false) => {
                                format!("{} ({}; hint: {})", error.message, location, hints)
                            }
                            (Some(location), true) => {
                                format!("{} ({})", error.message, location)
                            }
                            (None, false) => {
                                format!("{} (hint: {})", error.message, hints)
                            }
                            (None, true) => error.message.to_string(),
                        }
                    })
                    .collect::<Vec<_>>()
                    .join("\n");

                EcoString::from(message)
            })?;
            (doc, warnings.to_vec())
        }
    };

    let buffers = compiler::export(&document, format, ppi, standards)?;
    Ok((buffers, warnings))
}

fn vec_to_raw<T>(vec: Vec<T>) -> (*mut T, usize) {
    let boxed = vec.into_boxed_slice();
    let len = boxed.len();
    (Box::into_raw(boxed) as *mut T, len)
}

fn string_to_raw(s: String) -> (*mut u8, usize) {
    vec_to_raw(s.into_bytes())
}

unsafe fn free_raw_slice<T>(ptr: *mut T, len: usize) {
    if !ptr.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len));
    }
}

fn make_error_result(msg: impl Into<String>) -> CompileResult {
    let (error_ptr, error_len) = string_to_raw(msg.into());
    CompileResult {
        buffers: ptr::null_mut(),
        buffers_len: 0,
        warnings: ptr::null_mut(),
        warnings_len: 0,
        error_ptr,
        error_len,
    }
}

/// Parses one PDF standard name.
///
/// The accepted names are the ones `typst_pdf::PdfStandard` itself serialises to,
/// rather than a table restated here, so a standard added by a later Typst release is
/// accepted without a change to this function. `PdfStandard` is `#[non_exhaustive]`,
/// which makes matching on it exhaustively impossible from outside the crate anyway.
///
/// Input:  "a-2b"   Output: PdfStandard::A_2b
/// Input:  "UA-1"   Output: PdfStandard::Ua_1
/// Input:  "v-1.7"  Output: PdfStandard::V_1_7
/// Input:  "a-9z"   Output: None
/// Input:  "v-a-2b" Output: None
fn parse_pdf_standard(name: &str) -> Option<typst_pdf::PdfStandard> {
    fn by_serialised_name(name: &str) -> Option<typst_pdf::PdfStandard> {
        serde_json::from_value(serde_json::Value::String(name.to_owned())).ok()
    }

    // Standard names are ASCII, so full Unicode case folding would buy nothing.
    let lowered = name.to_ascii_lowercase();

    // Typst spells the plain PDF versions as bare numbers such as `1.7`, while the
    // README documents them as `v-1.7`, so the prefix is accepted as an alias. It
    // only applies to a version number; `v-a-2b` is not a spelling of anything.
    by_serialised_name(&lowered).or_else(|| {
        lowered
            .strip_prefix("v-")
            .filter(|rest| rest.starts_with(|c: char| c.is_ascii_digit()))
            .and_then(by_serialised_name)
    })
}

/// Parses the comma-separated list of PDF standards that arrives over the boundary.
/// Blank entries are ignored, so a trailing comma is not an error.
///
/// Input:  " a-2b , ua-1 "  Output: [PdfStandard::A_2b, PdfStandard::Ua_1]
fn parse_pdf_standards(list: &str) -> Result<Vec<typst_pdf::PdfStandard>, String> {
    list.split(',')
        .map(str::trim)
        .filter(|name| !name.is_empty())
        .map(|name| {
            parse_pdf_standard(name).ok_or_else(|| format!("Invalid PDF standard: {name}"))
        })
        .collect()
}

fn compile_internal(
    compiler: *mut Compiler,
    format_ptr: *const std::os::raw::c_char,
    ppi: f32,
    pdf_standards: *const std::os::raw::c_char,
) -> CompileResult {
    if compiler.is_null() {
        return make_error_result("Null compiler pointer");
    }
    let compiler = unsafe { &mut *compiler };
    let format_str = if format_ptr.is_null() {
        "pdf"
    } else {
        unsafe { std::ffi::CStr::from_ptr(format_ptr).to_str().unwrap_or("pdf") }
    };

    let standards_str = if pdf_standards.is_null() {
        ""
    } else {
        unsafe { std::ffi::CStr::from_ptr(pdf_standards).to_str().unwrap_or("") }
    };

    let standards = match parse_pdf_standards(standards_str) {
        Ok(standards) => standards,
        Err(message) => return make_error_result(message),
    };

    match compile_inner(&mut compiler.0, format_str, ppi, &standards) {
        Ok((buffers, warnings)) => {
            let c_buffers: Vec<Buffer> = buffers
                .into_iter()
                .map(|b| {
                    let (ptr, len) = vec_to_raw(b);
                    Buffer { ptr, len }
                })
                .collect();

            let c_warnings: Vec<Warning> = warnings
                .into_iter()
                .map(|w| {
                    let (message_ptr, message_len) = string_to_raw(w.message.to_string());
                    Warning { message_ptr, message_len }
                })
                .collect();

            let (buffers_ptr, buffers_len) = vec_to_raw(c_buffers);
            let (warnings_ptr, warnings_len) = vec_to_raw(c_warnings);

            CompileResult {
                buffers: buffers_ptr,
                buffers_len,
                warnings: warnings_ptr,
                warnings_len,
                error_ptr: ptr::null_mut(),
                error_len: 0,
            }
        }
        Err(err) => make_error_result(err.to_string()),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn compile(
    compiler: *mut Compiler,
    format_ptr: *const std::os::raw::c_char,
    ppi: f32,
    pdf_standards: *const std::os::raw::c_char,
) -> CompileResult {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        compile_internal(compiler, format_ptr, ppi, pdf_standards)
    }));

    match result {
        Ok(res) => res,
        Err(err) => {
            let msg = if let Some(s) = err.downcast_ref::<&str>() {
                *s
            } else if let Some(s) = err.downcast_ref::<String>() {
                s.as_str()
            } else {
                "Unknown panic"
            };
            make_error_result(format!("Panic: {}", msg))
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn free_compile_result(result: CompileResult) {
    unsafe {
        if !result.buffers.is_null() {
            let buffers = Box::from_raw(std::ptr::slice_from_raw_parts_mut(
                result.buffers,
                result.buffers_len,
            ));
            for buffer in buffers.iter() {
                free_raw_slice(buffer.ptr, buffer.len);
            }
        }
        if !result.warnings.is_null() {
            let warnings = Box::from_raw(std::ptr::slice_from_raw_parts_mut(
                result.warnings,
                result.warnings_len,
            ));
            for warning in warnings.iter() {
                free_raw_slice(warning.message_ptr, warning.message_len);
            }
        }
        if !result.error_ptr.is_null() {
            free_raw_slice(result.error_ptr, result.error_len);
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn reset_world() {
    comemo::evict(10);
}

#[cfg(test)]
mod tests {
    use super::{parse_pdf_standard, parse_pdf_standards};
    use typst_pdf::PdfStandard;

    #[test]
    fn pdf_versions_are_accepted_bare_and_with_the_v_prefix() {
        assert_eq!(parse_pdf_standard("1.4"), Some(PdfStandard::V_1_4));
        assert_eq!(parse_pdf_standard("v-1.4"), Some(PdfStandard::V_1_4));
        assert_eq!(parse_pdf_standard("1.7"), Some(PdfStandard::V_1_7));
        assert_eq!(parse_pdf_standard("v-1.7"), Some(PdfStandard::V_1_7));
        assert_eq!(parse_pdf_standard("2.0"), Some(PdfStandard::V_2_0));
        assert_eq!(parse_pdf_standard("v-2.0"), Some(PdfStandard::V_2_0));
    }

    #[test]
    fn archival_standards_are_accepted() {
        assert_eq!(parse_pdf_standard("a-1b"), Some(PdfStandard::A_1b));
        assert_eq!(parse_pdf_standard("a-2b"), Some(PdfStandard::A_2b));
        assert_eq!(parse_pdf_standard("a-3a"), Some(PdfStandard::A_3a));
        assert_eq!(parse_pdf_standard("a-4"), Some(PdfStandard::A_4));
        assert_eq!(parse_pdf_standard("a-4e"), Some(PdfStandard::A_4e));
    }

    /// The accessibility standard is what an obligation to publish accessible
    /// documents translates to, and the previous hand-written table left it out.
    #[test]
    fn the_accessibility_standard_is_accepted() {
        assert_eq!(parse_pdf_standard("ua-1"), Some(PdfStandard::Ua_1));
    }

    #[test]
    fn names_are_case_insensitive() {
        assert_eq!(parse_pdf_standard("A-2B"), Some(PdfStandard::A_2b));
        assert_eq!(parse_pdf_standard("UA-1"), Some(PdfStandard::Ua_1));
        assert_eq!(parse_pdf_standard("V-1.7"), Some(PdfStandard::V_1_7));
    }

    #[test]
    fn unknown_names_are_rejected() {
        assert_eq!(parse_pdf_standard("nonexistent-standard"), None);
        assert_eq!(parse_pdf_standard(""), None);
        // Neither half of the version alias is a standard on its own.
        assert_eq!(parse_pdf_standard("v-"), None);
        assert_eq!(parse_pdf_standard("a-9z"), None);
    }

    /// The `v-` prefix introduces a version number and nothing else, so it must not
    /// become a second spelling of every standard.
    #[test]
    fn the_version_prefix_applies_only_to_version_numbers() {
        assert_eq!(parse_pdf_standard("v-a-2b"), None);
        assert_eq!(parse_pdf_standard("v-ua-1"), None);
        assert_eq!(parse_pdf_standard("v-v-1.7"), None);
    }

    #[test]
    fn a_list_is_split_and_trimmed() {
        assert_eq!(
            parse_pdf_standards(" a-2b , ua-1 "),
            Ok(vec![PdfStandard::A_2b, PdfStandard::Ua_1])
        );
    }

    /// An empty list is how "no standard requested" arrives, and a stray comma is
    /// not worth failing a compilation over.
    #[test]
    fn blank_entries_are_ignored() {
        assert_eq!(parse_pdf_standards(""), Ok(vec![]));
        assert_eq!(parse_pdf_standards("  "), Ok(vec![]));
        assert_eq!(parse_pdf_standards("a-2b,"), Ok(vec![PdfStandard::A_2b]));
    }

    #[test]
    fn the_first_unknown_name_is_reported() {
        assert_eq!(
            parse_pdf_standards("a-2b,nonexistent-standard"),
            Err("Invalid PDF standard: nonexistent-standard".to_owned())
        );
        assert_eq!(
            parse_pdf_standards("nonexistent-standard,a-2b"),
            Err("Invalid PDF standard: nonexistent-standard".to_owned())
        );
    }
}
