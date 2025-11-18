# typstsharp

A .NET 10.0 wrapper around the Typst rendering stack. The managed layer in `src/typstsharp` calls into the Rust `typst_core` crate via P/Invoke and exposes convenient helpers for C# consumers plus a simple CLI.

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/) – required to build the managed projects.
- [Rust toolchain](https://www.rust-lang.org/tools/install) with `cargo` on your `PATH` – used to build the native `typst_core` cdylib.

## Building

```pwsh
# from the repository root
 dotnet build typstsharp.sln
```

The build will automatically:

1. Run `cargo build --release` on `src/typst_core` for each target runtime identifier (RID). By default, this includes `win-x64`, `linux-x64`, and others. For local debug builds, it only builds for the host architecture.
2. Stage the produced native libraries under `obj/`.
3. Add the libraries to the managed project's runtime assets so that `dotnet publish`/`dotnet pack` place the files under `runtimes/<rid>/native/` in the final artifact.
4. For local development, the native binary for the host architecture is copied to the output directory of any project referencing `typstsharp`, ensuring it's available for debugging.

You can override the target runtimes by setting the `RustTargets` property (e.g., `dotnet build -p:RustTargets=win-x64`).

## Verifying the CLI

```pwsh
# after a successful build
 dotnet run --project src/typstsharp.cli/typstsharp.cli.csproj
```

Because the Rust binary is registered as a runtime asset, `typst_core.dll`/`libtypst_core.so` will appear beside the CLI executable automatically.

## Usage

Here's a minimal example of how to use `typstsharp` to compile a document:

```csharp
using typstsharp;
using System.IO;

// The Typst source code
var input = """
    #let title = sys.inputs.title
    = Hello, #title!
    """;

// Create a compiler instance
using var client = new TypstCompiler(input);

// Set system inputs, which are accessible from the Typst script
var sysInputs = new Dictionary<string, object>
{
    { "title", "World" }
};
client.SetSysInputs(sysInputs);

// Compile the document
var output = client.Compile();

// Save the output to a file
File.WriteAllBytes("output.pdf", output.Buffers[0]);
```

## Notes

- If you need to inspect the generated P/Invoke bindings, see `src/typstsharp/Bindings.g.cs` (created via `csbindgen` during the Rust build script).
- The native Rust layer is responsible for memory management of the Typst world. The `TypstCompiler` class is `IDisposable` and should be properly disposed to release native resources.
