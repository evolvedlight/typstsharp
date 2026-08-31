//! The C ABI that the managed `typstsharp` package binds to.
//!
//! Every exported function takes or returns raw pointers, so the ownership rules are the contract
//! between this crate and its caller. They are stated on each function; the parts a caller has to
//! rely on across calls are:
//!
//! - A `Compiler` is created by [`create_compiler`] and lives until [`free_compiler`]. It is not
//!   synchronised: one compiler must not be used from two threads at once.
//! - A [`CompileResult`] owns its buffers, warning messages and error message. Each is an
//!   independent heap allocation, and none of them borrows from the compiler that produced them.
//! - Those allocations stay valid until [`free_compile_result`] is called on the result that owns
//!   them, whatever else happens in between: further [`compile`] calls, [`set_sys_inputs`],
//!   [`free_compiler`] on the originating compiler, or [`reset_world`].
//! - [`free_compile_result`] may be called from any thread, and must be called exactly once per
//!   result. Calling it twice frees the same allocations twice.
//!
//! Callers may therefore hold a result and read from its buffers for as long as they like, which is
//! what lets the managed side hand out the rendered document without copying it.

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

/// The stateful Typst compilation world, kept alive across compilations so that the incremental
/// cache can be reused.
pub struct Compiler(SystemWorld);

/// One rendered output: the whole document for PDF export, one page for PNG and SVG.
///
/// The bytes are owned by the [`CompileResult`] that contains this buffer and are freed by
/// [`free_compile_result`]. They are not NUL-terminated; `len` is the only length.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct Buffer {
    pub ptr: *mut u8,
    pub len: usize,
}

/// One warning emitted by a compilation that nevertheless succeeded.
///
/// `message_ptr` is UTF-8 and is not NUL-terminated, so it must be read with `message_len`. A
/// message may itself contain NUL bytes, because Typst diagnostics quote the source.
#[repr(C)]
pub struct Warning {
    pub message_ptr: *mut u8,
    pub message_len: usize,
}

/// The outcome of one [`compile`] call, owning everything it points at.
///
/// Either `error_ptr` is non-null and the compilation failed, or it is null and `buffers` holds the
/// rendered output. `warnings` may be populated in both cases. Every allocation reachable from here
/// is released by [`free_compile_result`], and by nothing else.
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

/// Creates a compiler that reads its document either from `input_path` or from `input_source`.
///
/// # Safety
///
/// `root`, `input_path`, `package_path` and `sys_inputs` must be null or NUL-terminated strings,
/// `font_paths` must be null or point to `font_paths_len` such strings, and `input_source` must be
/// null or point to `input_source_len` bytes. Unlike the others, the source is passed with an
/// explicit length and may contain NUL bytes. All of them need only stay valid for the duration of
/// the call. The returned compiler is owned by the caller and must be released with
/// [`free_compiler`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn create_compiler(
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

/// Releases a compiler created by [`create_compiler`]. A null pointer is ignored.
///
/// Results previously returned by [`compile`] are unaffected: they own their memory and stay valid.
///
/// # Safety
///
/// `compiler` must be null or a pointer returned by [`create_compiler`] that has not already been
/// freed, and no other thread may be using it.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_compiler(compiler: *mut Compiler) {
    if !compiler.is_null() {
        unsafe {
            let _ = Box::from_raw(compiler);
        }
    }
}

/// Replaces the `sys.inputs` dictionary the next compilation will see. Returns `false` if the
/// compiler is null, the JSON does not parse, or Typst rejects the dictionary.
///
/// # Safety
///
/// `compiler` must be null or a live pointer from [`create_compiler`], and `sys_inputs` must be
/// null or a NUL-terminated JSON object that stays valid for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn set_sys_inputs(compiler: *mut Compiler, sys_inputs: *const c_char) -> bool {
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

    let mut standards = Vec::new();
    if !standards_str.is_empty() {
        for s in standards_str.split(',') {
            let s = s.trim();
            if !s.is_empty() {
                let parsed = match s.to_lowercase().as_str() {
                    "1.4" | "v-1.4" => Some(typst_pdf::PdfStandard::V_1_4),
                    "1.5" | "v-1.5" => Some(typst_pdf::PdfStandard::V_1_5),
                    "1.6" | "v-1.6" => Some(typst_pdf::PdfStandard::V_1_6),
                    "1.7" | "v-1.7" => Some(typst_pdf::PdfStandard::V_1_7),
                    "2.0" | "v-2.0" => Some(typst_pdf::PdfStandard::V_2_0),
                    "a-1b" => Some(typst_pdf::PdfStandard::A_1b),
                    "a-1a" => Some(typst_pdf::PdfStandard::A_1a),
                    "a-2b" => Some(typst_pdf::PdfStandard::A_2b),
                    "a-2u" => Some(typst_pdf::PdfStandard::A_2u),
                    "a-2a" => Some(typst_pdf::PdfStandard::A_2a),
                    "a-3b" => Some(typst_pdf::PdfStandard::A_3b),
                    "a-3u" => Some(typst_pdf::PdfStandard::A_3u),
                    "a-3a" => Some(typst_pdf::PdfStandard::A_3a),
                    "a-4" => Some(typst_pdf::PdfStandard::A_4),
                    "a-4f" => Some(typst_pdf::PdfStandard::A_4f),
                    "a-4e" => Some(typst_pdf::PdfStandard::A_4e),
                    _ => None,
                };
                if let Some(std) = parsed {
                    standards.push(std);
                } else {
                    return make_error_result(format!("Invalid PDF standard: {}", s));
                }
            }
        }
    }

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

/// Compiles the document to `format`, which is one of `pdf`, `png` or `svg`.
///
/// The returned [`CompileResult`] owns its buffers and messages. They do not borrow from
/// `compiler`, so they outlive further compilations, [`set_sys_inputs`], [`reset_world`] and even
/// [`free_compiler`] on the compiler that produced them. The caller must pass the result to
/// [`free_compile_result`] exactly once.
///
/// A panic inside Typst is caught and reported as an error result rather than unwinding across the
/// ABI boundary.
///
/// # Safety
///
/// `compiler` must be null or a live pointer from [`create_compiler`] that no other thread is
/// using. `format_ptr` and `pdf_standards` must be null or NUL-terminated strings that stay valid
/// for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn compile(
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

/// Releases every allocation owned by a [`CompileResult`]: the buffers, the warning messages and
/// the error message. May be called from any thread.
///
/// # Safety
///
/// `result` must be a value returned by [`compile`] that has not already been passed to this
/// function, and nothing may read from its buffers afterwards. Calling this twice on the same
/// result frees the same allocations twice.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_compile_result(result: CompileResult) {
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

/// Trims the process-global incremental compilation cache. It holds no references to any
/// [`CompileResult`], so trimming it never invalidates output the caller is still holding.
#[unsafe(no_mangle)]
pub extern "C" fn reset_world() {
    comemo::evict(10);
}
