# Release Notes

## [Unreleased]
### Fixed
- Fixed a stream from `document.OpenOutputStream()` leaving the rendered document in `ArrayPool<byte>.Shared`. Only the byte-array reads were overridden, so span-shaped and asynchronous reads, and `CopyTo`, fell through to `Stream`, which stages the bytes through a rented array and returns it to the pool without clearing it. The next component in the process to rent from that pool could read the document back. The reads and the copy now go straight from native memory, which also removes a second copy of every byte: a 4 MiB span read measured 0.767 ms before and 0.274 ms after.
- Fixed an input path in a subfolder aborting the process instead of compiling. The path was handed to Typst verbatim, and a Typst virtual path only accepts forward slashes, so on Windows an ordinary relative path such as `templates\letter.typ` panicked inside a native call and took the host process down with it. Paths are now resolved against the project root before being converted, and a path that genuinely leaves the root reports an error instead of panicking. Note that the root is matched against an absolute input path textually, so on Windows both have to be spelled with the same casing.
- Fixed `ua-1` (PDF/UA-1, the accessibility standard) being rejected by `pdfStandards`. The accepted names are now taken from `typst_pdf::PdfStandard` itself, so every standard Typst supports is accepted, including ones added by later Typst releases. The documented `v-` prefix on plain PDF versions still works, and applies only to them: `v-a-2b` is not a spelling of `a-2b`.

### Changed
- **Breaking:** an invalid combination of PDF standards now fails the compilation instead of silently producing an ordinary PDF. The validation error from `PdfStandards::new` was discarded and export fell back to the default, so a pipeline could believe it was writing PDF/A while it was not. Combinations such as two PDF/A levels, or a PDF/A level that contradicts the requested PDF version, now throw with the message and hints from Typst. Callers passing a contradictory combination today receive a document and will receive an exception after this change.
- Note for PDF/A and PDF/UA: the exporter deliberately writes no timestamp, so the document has to carry its own date (`#set document(date: ...)`) and, for PDF/UA, a title and language.
- `compiler.CompilePdf(Stream)`, `compiler.CompilePdfAsync(Stream)`, `compiler.CompilePdf(string outputFile)` and `compiler.CompilePdfAsync(string outputFile)` now stream the document straight from native memory to the destination and return the compiler warnings, rather than returning a `PdfResult` that had to be materialised on the managed heap first. Use `compiler.CompilePdf()` when you want the bytes.
- `compiler.Compile(outputFile, format)` and `compiler.CompileSvg(...)` no longer copy the rendered output onto the managed heap before writing or decoding it.

### Added
 - Added `compiler.CompileToDocument(...)`, returning a disposable `TypstDocument` that exposes the rendered output while it is still in the memory the native library allocated. `GetOutputSpan`, `OpenOutputStream`, `CopyOutputTo`, `WriteOutputToFile` and `RentOutput` read it without putting a multi-megabyte PDF on the large object heap; `GetOutputBytes` copies when a `byte[]` is what you need.
 - Added easier PDF compilation APIs on `TypstCompiler`:
   - `compiler.CompilePdf(...)` returning a `PdfResult` (with implicit conversion to `byte[]`, `ReadOnlySpan<byte>`, and `ReadOnlyMemory<byte>`).
   - `compiler.CompilePdf(string outputFile)` / `compiler.CompilePdfAsync(string outputFile)` for direct file saving.
   - `compiler.CompilePdf(Stream destination)` / `compiler.CompilePdfAsync(Stream destination)` for stream output.
   - Static `TypstCompiler.CompilePdf(...)` and `TypstCompiler.CompilePdfFromFile(...)` for quick one-line compilations.
   - `compiler.CompileSvg(...)` and `compiler.CompilePng(...)` for format-specific image export.
   - `outcome.AsPdf()` and `outcome.PrimaryBuffer` on `CompileOutcome`.
- Added `includeSystemPackages` to `TypstCompiler`. Setting it to `false` resolves packages from `packagePath` only, so an import that is not vendored there fails instead of being downloaded from Typst Universe and compilation stays off the network.
- Added support for compiling Typst documents with multiple PDF standards simultaneously (e.g. `v-1.7`, `a-2b`, etc.) by exposing a `pdfStandards` parameter in the `TypstCompiler.Compile` API, leveraging the underlying `typst-pdf` crate updates in Typst 0.15.

