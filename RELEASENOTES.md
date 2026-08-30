# Release Notes

## [Unreleased]
### Added
- Added `TypstCompiler.CompileToDocument`, returning a disposable `TypstDocument` that keeps the rendered output in native memory allocated by Typst. The output can be read as a `ReadOnlySpan<byte>`, opened as a seekable `Stream`, written straight to a file, or copied into a pooled buffer (`ArrayPool<byte>`), so serving or uploading a PDF avoids allocating on the large object heap.
- Added ergonomic PDF and image compilation APIs on `TypstCompiler`:
  - `compiler.CompilePdf(...)` returning a `PdfResult` (with implicit conversion to `byte[]`, `ReadOnlySpan<byte>`, and `ReadOnlyMemory<byte>`).
  - `compiler.CompilePdf(string outputFile)` / `compiler.CompilePdfAsync(string outputFile)` for direct file saving.
  - `compiler.CompilePdf(Stream destination)` / `compiler.CompilePdfAsync(Stream destination)` for stream output.
  - Static `TypstCompiler.CompilePdf(...)` and `TypstCompiler.CompilePdfFromFile(...)` for quick one-line compilations.
  - `compiler.CompileSvg(...)` and `compiler.CompilePng(...)` for format-specific image export, plus `TypstCompiler.CompileSvg(...)` and `TypstCompiler.CompilePng(...)` static helpers.
  - `outcome.AsPdf()` and `outcome.PrimaryBuffer` on `CompileOutcome`.
- Added `TypstCompiler.CompileToStream`/`CompileToStreamAsync` and `CompileToFile`/`CompileToFileAsync` for zero-copy streaming and file writes across formats.
- Added `includeSystemPackages` to `TypstCompiler`. Setting it to `false` resolves packages from `packagePath` only, so an import that is not vendored there fails instead of being downloaded from Typst Universe and compilation stays off the network.
- Added support for compiling Typst documents with multiple PDF standards simultaneously (e.g. `v-1.7`, `a-2b`, etc.) by exposing a `pdfStandards` parameter in the `TypstCompiler.Compile` API, leveraging the underlying `typst-pdf` crate updates in Typst 0.15.

### Changed
- `TypstCompiler.Compile(outputFile, format)` now streams each output buffer from native memory to the file handle instead of materialising it as a `byte[]` first. File sharing, overwrite semantics and the multi-page file naming are unchanged.
