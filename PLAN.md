# Plan: Simplify to Single Universal Linux Asset

## Objective
Verify if we can distribute the statically linked Musl binary under the single RID `linux-x64` and have it work on both standard Linux (Ubuntu) and Musl Linux (Alpine). This would simplify the project structure and NuGet package.

## Steps

1.  **Modify Project Configuration**
    *   Edit `src/typstsharp/typstsharp.csproj`.
    *   Remove the `linux-musl-x64` item from `<RustRuntime>`.
    *   Keep `linux-x64` pointing to the Musl target (`x86_64-unknown-linux-musl`).

2.  **Verify Build & Packaging**
    *   Run `local-ci/test-build.sh`.
    *   Ensure the generated NuGet package only contains `runtimes/linux-x64/...` and NOT `runtimes/linux-musl-x64/...`.

3.  **Verify Compatibility**
    *   **Ubuntu 22.04:** Rebuild and run `typstsharp-repro`. It should pick up the `linux-x64` asset and run (as verified previously).
    *   **Alpine:** Rebuild and run `typstsharp-repro-alpine`. It should fallback to using the `linux-x64` asset (since `linux-musl-x64` is missing) and run successfully because the binary is compatible.

4.  **Result**
    *   If both pass, we have a single, clean Linux asset that works everywhere.
    *   If Alpine fails to pick up the asset, we must keep the separate RID, but the binary strategy remains correct.
