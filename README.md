# typstsharp

A .NET 10.0 wrapper around the Typst rendering stack. The managed layer in `src/typstsharp` calls into the Rust `typst_core` crate via P/Invoke and exposes convenient helpers for C# consumers plus a simple CLI.

## Using

A simple example:

```csharp
#:package typstsharp@0.0.8

using typstsharp;

var compiler = TypstCompiler.FromSource("= Hello World!");
var result = compiler.Compile();

var file = result.Buffers[0];
await File.WriteAllBytesAsync("output.pdf", file);
Console.WriteLine("PDF generated: output.pdf");

// Open the generated PDF file (works on Windows)
System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("output.pdf") { UseShellExecute = true });
```

A more complicated example where we bulk generate PDFs:
```csharp
#:package typstsharp@0.0.8

using typstsharp;

var typstInput = """
#let (
  first-name,
  points-balance,
) = sys.inputs

#set page(header: align(
  right + bottom,
  text("Logo"),
))
#set text(font: "IBM Plex Sans")

Hello *#first-name,*

You have accrued
#underline[#points-balance]
GlorboCorp Rewards Points
last year!
""";

var compiler = TypstCompiler.FromSource(typstInput);
Directory.CreateDirectory("output");

var people = new Dictionary<string, int>
{
    ["Alice"] = 1200,
    ["Bob"] = 850,
    ["Charlie"] = 4300,
};

foreach (var (person, balance) in people)
{
    compiler.SetSysInputs(new Dictionary<string, string>
    {
        ["first-name"] = person,
        ["points-balance"] = balance.ToString(),
    });

    var result = compiler.Compile();

    var file = result.Buffers[0];
    await File.WriteAllBytesAsync($"output/output{person}.pdf", file);
    Console.WriteLine($"PDF generated: output{person}.pdf");
}

System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("output") { UseShellExecute = true });
```


You can easily use this inside of an ASP.Net Server (just ensure you lazy load and cache the TypstCompiler to reduce from 40ms to around 3ms for a normal compile).

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/) – required to build the managed projects.
- [Rust toolchain](https://www.rust-lang.org/tools/install) (with `cargo`) – **only required if you are building the project from source.** The NuGet package includes pre-compiled native binaries.

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

## Notes

- If you need to inspect the generated P/Invoke bindings, see `src/typstsharp/Bindings.g.cs` (created via `csbindgen` during the Rust build script).
- The native Rust layer is responsible for memory management of the Typst world. The `TypstCompiler` class is `IDisposable` and should be properly disposed to release native resources.
