# typstsharp

A .NET 10.0 wrapper around the Typst 0.15 rendering stack. The managed layer in `src/typstsharp` calls into the Rust `typst_core` crate via P/Invoke and exposes convenient helpers for C# consumers plus a simple CLI.

For the latest changes, see our [Release Notes](RELEASENOTES.md).

## Using

A simple example:

```csharp
#:package typstsharp@0.0.8

using typstsharp;

// Direct one-liner compilation
byte[] pdf = TypstCompiler.CompilePdf("= Hello World!");
await File.WriteAllBytesAsync("output.pdf", pdf);

// Or compile and save directly:
// TypstCompiler.CompilePdf("= Hello World!").Save("output.pdf");

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

using var compiler = TypstCompiler.FromSource(typstInput);
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

    await compiler.CompilePdfAsync($"output/output{person}.pdf");
    Console.WriteLine($"PDF generated: output{person}.pdf");
}

System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("output") { UseShellExecute = true });
```

### PDF Standards (Typst 0.15+)
You can export documents using specific PDF standards (like PDF/A or PDF/X) by passing them to `CompilePdf()`. You can even specify multiple standards at once:

```csharp
using var compiler = TypstCompiler.FromSource("= Archival Document");
var pdf = compiler.CompilePdf(pdfStandards: new[] { "a-2b", "v-1.7" });

await pdf.SaveAsync("archival.pdf");
```

### Exporting SVG and PNG Images

You can compile documents directly to SVG (vector) or PNG (raster) images:

#### Single SVG (Equations, Diagrams, Icons)
Typst can tightly fit content onto a single page using `#set page(width: auto, height: auto, margin: ...)`:

```csharp
var mathSnippet = """
#set page(width: auto, height: auto, margin: 5pt)
$ integral_0^infinity e^(-x^2) dif x = sqrt(pi)/2 $
""";

// One-liner: compile and get the SVG string directly
string svg = TypstCompiler.CompileSvg(mathSnippet);

// Or save it to a file
await TypstCompiler.CompileSvg(mathSnippet).SaveAsync("formula.svg");
```

#### Multi-Page SVG / PNG
For multi-page documents, `CompileSvg()` and `CompilePng()` return collection results with one item per page:

```csharp
using var compiler = TypstCompiler.FromSource("= Page 1\n#pagebreak()\n= Page 2");

var svgResult = compiler.CompileSvg();
Console.WriteLine($"Generated {svgResult.Count} SVG pages");
string page1Svg = svgResult[0];
string page2Svg = svgResult[1];

// PNG export with custom PPI (default 144)
var pngResult = compiler.CompilePng(ppi: 300);
byte[] page1Png = pngResult[0];
```

### Packages

`packagePath` points the compiler at a directory of Typst packages, laid out as
`<namespace>/<name>/<version>` just like the machine-wide package directory. It is searched first,
and anything not found there still falls back to the machine-wide directories and, for `@preview`,
to a download from Typst Universe.

Pass `includeSystemPackages: false` to drop those fallbacks. Packages then resolve from
`packagePath` alone, an import that is not vendored there fails with `package not found` instead of
being fetched, and compilation never touches the network:

```csharp
var compiler = TypstCompiler.FromFile(
    "label.typ",
    packagePath: Path.Combine(AppContext.BaseDirectory, "TypstPackages"),
    includeSystemPackages: false);
```

This is what an application that ships its packages alongside its binaries wants, and it keeps
builds reproducible: whatever is deployed is exactly what gets compiled.

You can easily use this inside of an ASP.Net Server (just ensure you lazy load and cache the TypstCompiler to reduce from 40ms to around 3ms for a normal compile).

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/) – required to build the managed projects.
- [Rust toolchain](https://www.rust-lang.org/tools/install) (with `cargo`) – **only required if you are building the project from source.** The NuGet package includes pre-compiled native binaries.

## Building

```pwsh
# from the repository root
 dotnet build typstsharp.slnx
```

The build will automatically:

1. Run `cargo build --release` on `src/typst_core` for each target runtime identifier (RID). By default, this includes `win-x64`, `linux-x64`, and others. For local debug builds, it only builds for the host architecture. Note that the build will automatically fallback to `gnu` from `musl` on Linux if `musl-gcc` is not available on the system.
2. Stage the produced native libraries under `obj/`.
3. Add the libraries to the managed project's runtime assets so that `dotnet publish`/`dotnet pack` place the files under `runtimes/<rid>/native/` in the final artifact.
4. For local development, the native binary for the host architecture is copied to the output directory of any project referencing `typstsharp`, ensuring it's available for debugging.

You can override the target runtimes by setting the `RustTargets` property (e.g., `dotnet build -p:RustTargets=win-x64`). On macOS, use `osx-arm64` for Apple Silicon or `osx-x64` for Intel:

```bash
dotnet build -p:RustTargets=osx-arm64
```

## Verifying the CLI

```pwsh
# after a successful build
 dotnet run --project src/typstsharp.cli/typstsharp.cli.csproj
```

Because the Rust binary is registered as a runtime asset, `typst_core.dll`, `libtypst_core.so`, or `libtypst_core.dylib` will appear beside the CLI executable automatically.

## Notes

- If you need to inspect the generated P/Invoke bindings, see `src/typstsharp/Bindings.g.cs` (created via `csbindgen` during the Rust build script).
- The native Rust layer is responsible for memory management of the Typst world. The `TypstCompiler` class is `IDisposable` and should be properly disposed to release native resources.
