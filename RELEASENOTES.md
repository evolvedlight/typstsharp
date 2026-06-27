# Release Notes

## [Unreleased]
### Added
- Added support for compiling Typst documents with multiple PDF standards simultaneously (e.g. `v-1.7`, `a-2b`, etc.) by exposing a `pdfStandards` parameter in the `TypstCompiler.Compile` API, leveraging the underlying `typst-pdf` crate updates in Typst 0.15.
