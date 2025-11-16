# typstsharp

A .NET 10.0 wrapper around the Typst rendering stack. The managed layer in `src/typstsharp` calls into the Rust `rust_core` crate via P/Invoke and exposes convenient helpers for C# consumers plus a simple CLI.

## Prerequisites

- [.NET SDK 10.0 preview](https://dotnet.microsoft.com/) – required to build the managed projects.
- [Rust toolchain](https://www.rust-lang.org/tools/install) with `cargo` on your `PATH` – used to build the native `rust_core` cdylib.

## Building

```pwsh
# from the repository root
 dotnet build typstsharp.slnx
```

The build will automatically:

1. Run `cargo build` on `src/typst_core` using the corresponding Debug/Release profile.
2. Stage the produced native library under `obj/<tfm>/rust/native`.
3. Add the library to the managed project's runtime assets so that:
   - `rust_core` is copied next to every project that references `typstsharp`.
   - `dotnet publish`/`dotnet pack` place the file under `runtimes/<rid>/native/` in the final artifact.

You can override the runtime folder that gets stamped into packages by setting `RuntimeIdentifier` (for example `dotnet publish -r win-x64`). When no RID is provided, the host SDK's RID is used as a fallback.

## Verifying the CLI

```pwsh
# after a successful build
 dotnet run --project src/typstsharp.cli/typstsharp.cli.csproj
```

Because the Rust binary is registered as a runtime asset, `rust_core.dll`/`librust_core.*` will appear beside the CLI executable automatically.

## Notes

- The Rust crate currently targets the host triple; cross-compiling to other platforms requires using the appropriate Rust target toolchains and supplying a matching `RuntimeIdentifier` during build/publish.
- If you need to inspect the generated bindings, see `src/typstsharp/Bindings.g.cs` (created via `csbindgen` during the Rust build script).
- Native allocation tracking is built in: the Rust layer maintains atomic counts of live buffer/warning allocations. `TypstClient.GetAllocationStats()` and the CLI logging hook expose those numbers so you can spot leaks after large stress runs.

## Rust-only stress harness

If you want to profile the Rust code without .NET in the loop, use the standalone harness under `src/typst_core/harness`:

```pwsh
cd src/typst_core/harness
cargo run -- --iterations 100000 --report-every 10000 --output-dir ..\..\..\outtmp
```

The harness drives `create_compiler`/`compile` directly, dumps the first generated artifact (optional), and prints the same allocation stats as the managed CLI. This makes it easy to attach tools like Valgrind, heaptrack, or Windows UMDH solely to the Rust process.
