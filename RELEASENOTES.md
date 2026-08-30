# Release Notes

## [Unreleased]
### Added
 - Added easier PDF compilation APIs on `TypstCompiler`:
   - `compiler.CompilePdf(...)` returning a `PdfResult` (with implicit conversion to `byte[]`, `ReadOnlySpan<byte>`, and `ReadOnlyMemory<byte>`).
   - `compiler.CompilePdf(string outputFile)` / `compiler.CompilePdfAsync(string outputFile)` for direct file saving.
   - `compiler.CompilePdf(Stream destination)` / `compiler.CompilePdfAsync(Stream destination)` for stream output.
   - Static `TypstCompiler.CompilePdf(...)` and `TypstCompiler.CompilePdfFromFile(...)` for quick one-line compilations.
   - `compiler.CompileSvg(...)` and `compiler.CompilePng(...)` for format-specific image export.
   - `outcome.AsPdf()` and `outcome.PrimaryBuffer` on `CompileOutcome`.
- Added `includeSystemPackages` to `TypstCompiler`. Setting it to `false` resolves packages from `packagePath` only, so an import that is not vendored there fails instead of being downloaded from Typst Universe and compilation stays off the network.
- Added support for compiling Typst documents with multiple PDF standards simultaneously (e.g. `v-1.7`, `a-2b`, etc.) by exposing a `pdfStandards` parameter in the `TypstCompiler.Compile` API, leveraging the underlying `typst-pdf` crate updates in Typst 0.15.
