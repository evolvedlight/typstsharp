# Release Notes

## [Unreleased]
### Added
- Added `includeSystemPackages` to `TypstCompiler`. Setting it to `false` resolves packages from `packagePath` only, so an import that is not vendored there fails instead of being downloaded from Typst Universe and compilation stays off the network.
- Added support for compiling Typst documents with multiple PDF standards simultaneously (e.g. `v-1.7`, `a-2b`, etc.) by exposing a `pdfStandards` parameter in the `TypstCompiler.Compile` API, leveraging the underlying `typst-pdf` crate updates in Typst 0.15.
