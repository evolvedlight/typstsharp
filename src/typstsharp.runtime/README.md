# typstsharp native runtime package

This package contains the pre-compiled native `typst_core` library for a specific target runtime identifier (RID), used by [`typstsharp`](https://www.nuget.org/packages/typstsharp).

You normally do **not** need to install this package directly — install [`typstsharp`](https://www.nuget.org/packages/typstsharp) instead, which references the runtime packages automatically and lets the .NET SDK select the matching native binary for your platform.
