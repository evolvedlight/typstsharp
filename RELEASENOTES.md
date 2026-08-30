# Release Notes

## [Unreleased]
### Added
- Added `TypstCompiler.CompileToDocument`, returning a disposable `TypstDocument` that keeps the rendered output in the memory allocated by Typst. The output can be read as a `ReadOnlySpan<byte>`, opened as a seekable `Stream`, written straight to a file, or copied into a pooled buffer, so serving or uploading a PDF no longer has to allocate it on the large object heap.
- Added `TypstCompiler.CompileToStream`/`CompileToStreamAsync` for writing a document directly to a stream, and `CompileToFile`/`CompileToFileAsync` for writing it directly to disk. `CompileToFile` also avoids the trap of calling `Compile("out.pdf")`, which passes the path as the output format.
- Added support for compiling Typst documents with multiple PDF standards simultaneously (e.g. `v-1.7`, `a-2b`, etc.) by exposing a `pdfStandards` parameter in the `TypstCompiler.Compile` API, leveraging the underlying `typst-pdf` crate updates in Typst 0.15.

### Changed
- `TypstCompiler.Compile(outputFile, format)` now streams each output buffer from native memory to the file handle instead of materialising it as a `byte[]` first. File sharing, overwrite semantics and the multi-page file naming are unchanged.
