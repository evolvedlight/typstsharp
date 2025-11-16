fn main() {
    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .csharp_dll_name("rust_core")
        .generate_csharp_file("../typstsharp/Bindings.g.cs")
        .unwrap();
}
