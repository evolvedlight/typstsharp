//! Proves that clearing the compilation cache releases memory, rather than only that output stays
//! correct afterwards. A cache eviction that did nothing would pass every other test.
//!
//! This file is its own test binary so that it can count every heap allocation in the process with
//! a global allocator. It holds a single test on purpose: the cache and the allocator are both
//! process-wide, so a second test running in parallel would skew the counts.

use std::alloc::{GlobalAlloc, Layout, System};
use std::ffi::{c_char, CString};
use std::sync::atomic::{AtomicIsize, Ordering};

use typst_core::{
    compile, create_compiler, evict_cache, free_compile_result, free_compiler, set_sys_inputs,
};

/// Forwards to the system allocator and keeps a running total of the bytes currently allocated.
struct CountingAllocator;

static LIVE_BYTES: AtomicIsize = AtomicIsize::new(0);

unsafe impl GlobalAlloc for CountingAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let ptr = unsafe { System.alloc(layout) };
        if !ptr.is_null() {
            LIVE_BYTES.fetch_add(layout.size() as isize, Ordering::SeqCst);
        }
        ptr
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        let ptr = unsafe { System.alloc_zeroed(layout) };
        if !ptr.is_null() {
            LIVE_BYTES.fetch_add(layout.size() as isize, Ordering::SeqCst);
        }
        ptr
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) };
        LIVE_BYTES.fetch_sub(layout.size() as isize, Ordering::SeqCst);
    }

    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        let new_ptr = unsafe { System.realloc(ptr, layout, new_size) };
        if !new_ptr.is_null() {
            LIVE_BYTES.fetch_add(new_size as isize - layout.size() as isize, Ordering::SeqCst);
        }
        new_ptr
    }
}

#[global_allocator]
static ALLOCATOR: CountingAllocator = CountingAllocator;

fn live_bytes() -> isize {
    LIVE_BYTES.load(Ordering::SeqCst)
}

#[test]
fn clearing_the_cache_releases_the_memory_a_compilation_cached() {
    // Enough distinct content that the memoised layout dwarfs anything else a compilation keeps.
    // Every heading carries `sys.inputs.run`, so a compilation with a new value cannot reuse what
    // an earlier one cached, whether or not that was evicted.
    let source = "#set document(date: none)\n#let run = sys.inputs.at(\"run\")\n".to_owned()
        + &(0..100)
            .map(|page| format!("= Page {page} of run #run\n\n#lorem(150)\n\n#pagebreak()\n"))
            .collect::<String>();

    let root = CString::new(".").unwrap();
    let sys_inputs = CString::new(r#"{"run": "warm-up"}"#).unwrap();
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

    // A first compilation loads the fonts and parses the source into the compiler's own world.
    // Those stay with the compiler, so clearing the cache after it gives the baseline that a
    // compilation's cached results are measured against.
    let warm_up = unsafe { compile(compiler, std::ptr::null(), 96.0, 0.0, std::ptr::null()) };
    assert!(warm_up.error_ptr.is_null(), "valid document failed to compile");
    unsafe { free_compile_result(warm_up) };
    assert!(evict_cache(), "clearing the cache failed");
    let measured_inputs = CString::new(r#"{"run": "measured"}"#).unwrap();
    assert!(
        unsafe { set_sys_inputs(compiler, measured_inputs.as_ptr()) },
        "failed to set the inputs"
    );
    let before = live_bytes();

    let result = unsafe { compile(compiler, std::ptr::null(), 96.0, 0.0, std::ptr::null()) };
    assert!(result.error_ptr.is_null(), "valid document failed to compile");
    unsafe { free_compile_result(result) };
    let cached = live_bytes() - before;

    assert!(evict_cache(), "clearing the cache failed");
    let remaining = live_bytes() - before;

    eprintln!("cached after compiling: {cached} bytes, left after clearing: {remaining} bytes");
    assert!(
        cached > 1024 * 1024,
        "compiling 100 pages cached only {cached} bytes, so this test proves nothing"
    );
    assert!(
        remaining < cached / 10,
        "clearing the cache left {remaining} of {cached} cached bytes allocated"
    );

    unsafe { free_compiler(compiler) };
}
