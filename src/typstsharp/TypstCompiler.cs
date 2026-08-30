using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace typstsharp;

public record Fonts(
    bool IncludeSystemFonts = true,
    IEnumerable<string>? FontPaths = null
);

/// <summary>
/// A Typst compiler holding a native compilation world. Reusing one instance across renders is much
/// cheaper than creating one per render, so a server should cache it and call
/// <see cref="SetSysInputs"/> between compilations.
/// </summary>
/// <remarks>
/// An instance is not thread-safe: compiling mutates the native world, and the incremental cache it
/// trims is process-global, so two compilers on two threads interfere with each other. Serialise
/// access, or give each thread its own compiler.
/// </remarks>
public class TypstCompiler : IDisposable
{
    public static string EmptyDictionaryJson => "{}";
    private unsafe CsBindgen.Compiler* _compiler;
    private bool _disposed = false;
    private static readonly JsonSerializerOptions sourceGenOptions = new()
    {
        TypeInfoResolver = SourceGenerationContext.Default
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="TypstCompiler"/> class.
    /// </summary>
    /// <param name="inputPath">The path to the Typst source file to compile.</param>
    /// <param name="fonts">Font settings, including system fonts and custom font paths.</param>
    /// <param name="sysInputs">Initial system inputs (legacy, prefer SetSysInputs).</param>
    /// <exception cref="Exception">Thrown when the Typst compiler fails to initialize.</exception>
    public TypstCompiler(string inputPath, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null, string? packagePath = null)
        : this(inputPath, null, fonts, sysInputs, root, packagePath)
    {
    }

    /// <summary>
    /// Creates a new <see cref="TypstCompiler"/> from a source string.
    /// </summary>
    /// <param name="source">The Typst source code.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <returns>A new <see cref="TypstCompiler"/> instance.</returns>
    public static TypstCompiler FromSource(string source, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null, string? packagePath = null)
    {
        return new TypstCompiler(null, source, fonts, sysInputs, root, packagePath);
    }

    /// <summary>
    /// Creates a new <see cref="TypstCompiler"/> from a file path.
    /// </summary>
    /// <param name="path">The path to the Typst file.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <returns>A new <see cref="TypstCompiler"/> instance.</returns>
    public static TypstCompiler FromFile(string path, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null, string? packagePath = null)
    {
        return new TypstCompiler(path, null, fonts, sysInputs, root, packagePath);
    }

    

    private unsafe TypstCompiler(string? inputPath, string? inputSource, Fonts? fonts, Dictionary<string, string>? sysInputs, string? root, string? packagePath = null)
    {
        fonts ??= new Fonts();
        var fontPaths = fonts.FontPaths ?? [];
        bool ignoreSystemFonts = !fonts.IncludeSystemFonts;

        var inputPathPtr = inputPath != null ? Marshal.StringToCoTaskMemUTF8(inputPath) : IntPtr.Zero;
        var inputSourcePtr = inputSource != null ? Marshal.StringToCoTaskMemUTF8(inputSource) : IntPtr.Zero;
        
        IntPtr rootPtr = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(root))
        {
            rootPtr = Marshal.StringToCoTaskMemUTF8(root);
        }

        var fontPathsList = fontPaths.ToList();
        var fontPathPtrs = new IntPtr[fontPathsList.Count];
        for (int i = 0; i < fontPathsList.Count; i++)
        {
            fontPathPtrs[i] = Marshal.StringToCoTaskMemUTF8(fontPathsList[i]);
        }

        var packagePathPtr = packagePath != null ? Marshal.StringToCoTaskMemUTF8(packagePath) : IntPtr.Zero;

        var sysInputsJson = sysInputs == null ? "{}" : JsonSerializer.Serialize<Dictionary<string, string>>(sysInputs, sourceGenOptions);
        var sysInputsPtr = Marshal.StringToCoTaskMemUTF8(sysInputsJson);

        try
        {
            fixed (IntPtr* fontPathsRawPtr = fontPathPtrs)
            {
                IntPtr* fontPathsPtr = fontPathsList.Count == 0 ? null : fontPathsRawPtr;
                _compiler = CsBindgen.NativeMethods.create_compiler(
                    (byte*)rootPtr, 
                    (byte*)inputPathPtr, 
                    (byte*)inputSourcePtr, 
                    (byte**)fontPathsPtr, 
                    (nuint)fontPathsList.Count, 
                    (byte*)packagePathPtr,
                    (byte*)sysInputsPtr, 
                    ignoreSystemFonts);
            }

            if (_compiler == null)
            {
                throw new Exception("Failed to create Typst compiler.");
            }
        }
        finally
        {
            if (rootPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(rootPtr);
            if (inputPathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(inputPathPtr);
            if (inputSourcePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(inputSourcePtr);
            foreach (var ptr in fontPathPtrs) Marshal.FreeCoTaskMem(ptr);
            if (packagePathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(packagePathPtr);
            Marshal.FreeCoTaskMem(sysInputsPtr);
        }
    }

    /// <summary>
    /// Compiles the Typst document and hands back the rendered output while it is still in native
    /// memory, without copying it onto the managed heap. The caller decides whether to stream the
    /// bytes to disk, read them as a span, or copy them.
    /// </summary>
    /// <param name="format">The output format: "pdf", "png" or "svg".</param>
    /// <param name="ppi">The resolution used for raster output.</param>
    /// <param name="pdfStandards">PDF standards to conform to, e.g. "a-2b" or "v-1.7".</param>
    /// <returns>A <see cref="TypstDocument"/> that must be disposed to release the native memory.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the compilation fails, with the error message from Typst.</exception>
    /// <remarks>
    /// The returned document is independent of this compiler and stays valid after the compiler has
    /// been disposed.
    /// </remarks>
    public unsafe TypstDocument CompileToDocument(string format = "pdf", float ppi = 144.0f, IEnumerable<string>? pdfStandards = null)
    {
        EnsureNotDisposed();

        IntPtr formatPtr = Marshal.StringToCoTaskMemUTF8(format);
        string standardsStr = pdfStandards != null ? string.Join(",", pdfStandards) : "";
        IntPtr standardsPtr = Marshal.StringToCoTaskMemUTF8(standardsStr);

        try
        {
            var native = CsBindgen.NativeMethods.compile(_compiler, (byte*)formatPtr, ppi, (byte*)standardsPtr);

            // The P/Invoke is a preemptive-mode transition, so a collection can run while Typst is
            // compiling. Without this the compiler could be finalized, and the native world freed,
            // underneath the call that is using it.
            GC.KeepAlive(this);

            try
            {
                if (native.error != null)
                {
                    var error = Marshal.PtrToStringUTF8((nint)native.error) ?? "Unknown Typst error";
                    throw new InvalidOperationException(error);
                }

                // From here on the document owns the native result and frees it when disposed.
                return new TypstDocument(native);
            }
            catch
            {
                CsBindgen.NativeMethods.free_compile_result(native);
                throw;
            }
            finally
            {
                // Trims the incremental compilation cache; independent of who owns the buffers.
                CsBindgen.NativeMethods.reset_world();
            }
        }
        finally
        {
            if (formatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPtr);
            if (standardsPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(standardsPtr);
        }
    }

    /// <summary>
    /// Compiles the Typst document and copies the result onto the managed heap.
    /// </summary>
    /// <returns>A <see cref="CompileOutcome"/> containing the compiled document buffers and any warnings.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the compilation fails, with the error message from Typst.</exception>
    public CompileOutcome Compile(string format = "pdf", float ppi = 144.0f, IEnumerable<string>? pdfStandards = null)
    {
        using var document = CompileToDocument(format, ppi, pdfStandards);

        var managedBuffers = new List<byte[]>(document.OutputCount);
        for (int i = 0; i < document.OutputCount; i++)
        {
            managedBuffers.Add(document.GetOutputBytes(i));
        }

        return new CompileOutcome(managedBuffers, document.Warnings);
    }

    public record TypstWarning(string Message);

    /// <summary>
    /// Compiles the Typst document with the specified format and resolution.
    /// </summary>
    /// <param name="format">The output format: "pdf", "png" or "svg".</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    /// <returns>A tuple containing a list of byte arrays for each page and a list of warnings.</returns>
    public (List<byte[]> pages, List<TypstWarning> warnings) CompileToPages(string format = "pdf", float ppi = 144.0f, IEnumerable<string>? pdfStandards = null)
    {
        var outcome = Compile(format, ppi, pdfStandards);
        var pages = new List<byte[]>(outcome.Buffers);
        var warnings = outcome.Warnings
            .Select(message => new TypstWarning(message))
            .ToList();
        return (pages, warnings);
    }

    /// <summary>
    /// Compiles the Typst document and writes the output to one or more files, streaming the bytes
    /// from native memory to disk without buffering the document on the managed heap.
    /// </summary>
    /// <param name="outputFile">The path for the output file. If the format renders one buffer per page, a page number is appended to the file name for each page.</param>
    /// <param name="format">The output format: "pdf", "png" or "svg".</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    public void Compile(string outputFile, string format, float ppi = 144.0f, IEnumerable<string>? pdfStandards = null) =>
        CompileToFile(outputFile, format, ppi, pdfStandards);

    /// <summary>
    /// Compiles the Typst document and writes the output to one or more files, streaming the bytes
    /// from native memory to disk without buffering the document on the managed heap.
    /// </summary>
    /// <param name="outputFile">The path for the output file. If the format renders one buffer per page, a page number is appended to the file name for each page.</param>
    /// <param name="format">The output format: "pdf", "png" or "svg".</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    /// <param name="pdfStandards">PDF standards to conform to, e.g. "a-2b" or "v-1.7".</param>
    public void CompileToFile(string outputFile, string format = "pdf", float ppi = 144.0f, IEnumerable<string>? pdfStandards = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFile);

        using var document = CompileToDocument(format, ppi, pdfStandards);
        for (int i = 0; i < document.OutputCount; i++)
        {
            document.WriteOutputToFile(GetOutputPath(outputFile, i, document.OutputCount), i);
        }
    }

    /// <summary>
    /// Asynchronously compiles the Typst document and writes the output to one or more files,
    /// streaming the bytes from native memory to disk.
    /// </summary>
    /// <param name="outputFile">The path for the output file. If the format renders one buffer per page, a page number is appended to the file name for each page.</param>
    /// <param name="format">The output format: "pdf", "png" or "svg".</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    /// <param name="pdfStandards">PDF standards to conform to, e.g. "a-2b" or "v-1.7".</param>
    /// <param name="cancellationToken">Token to cancel the write. Compilation itself is synchronous and cannot be cancelled, and a cancelled multi-page write leaves the files written so far behind.</param>
    public async Task CompileToFileAsync(string outputFile, string format = "pdf", float ppi = 144.0f, IEnumerable<string>? pdfStandards = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFile);

        using var document = CompileToDocument(format, ppi, pdfStandards);
        for (int i = 0; i < document.OutputCount; i++)
        {
            await document.WriteOutputToFileAsync(GetOutputPath(outputFile, i, document.OutputCount), i, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Compiles the Typst document and writes it to <paramref name="destination"/> without
    /// buffering it on the managed heap.
    /// </summary>
    /// <param name="destination">The stream to write the document to.</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    /// <param name="pdfStandards">PDF standards to conform to, e.g. "a-2b" or "v-1.7".</param>
    /// <param name="format">The output format. Only "pdf" renders the whole document into a single buffer; use <see cref="CompileToDocument"/> for the per-page formats.</param>
    public void CompileToStream(Stream destination, string format = "pdf", float ppi = 144.0f, IEnumerable<string>? pdfStandards = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        EnsureSingleBufferFormat(format);

        using var document = CompileToDocument(format, ppi, pdfStandards);
        EnsureSingleBuffer(document);
        document.CopyOutputTo(destination);
    }

    /// <summary>
    /// Asynchronously compiles the Typst document and writes it to <paramref name="destination"/>
    /// without buffering it on the managed heap.
    /// </summary>
    /// <param name="destination">The stream to write the document to.</param>
    /// <param name="format">The output format. Only "pdf" renders the whole document into a single buffer; use <see cref="CompileToDocument"/> for the per-page formats.</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    /// <param name="pdfStandards">PDF standards to conform to, e.g. "a-2b" or "v-1.7".</param>
    /// <param name="cancellationToken">Token to cancel the write. Compilation itself is synchronous and cannot be cancelled.</param>
    public async Task CompileToStreamAsync(Stream destination, string format = "pdf", float ppi = 144.0f, IEnumerable<string>? pdfStandards = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        EnsureSingleBufferFormat(format);

        using var document = CompileToDocument(format, ppi, pdfStandards);
        EnsureSingleBuffer(document);
        await document.CopyOutputToAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects the per-page formats before compiling. Checking only the buffer count afterwards
    /// would make the failure depend on the content: a PNG label that happens to fit on one page
    /// would succeed until a longer address pushed it onto a second one.
    /// </summary>
    private static void EnsureSingleBufferFormat(string format)
    {
        if (!string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{format}' renders one buffer per page and cannot be written to a single stream. " +
                $"Use {nameof(CompileToDocument)} and write each buffer individually.",
                nameof(format));
        }
    }

    private static void EnsureSingleBuffer(TypstDocument document)
    {
        if (document.OutputCount != 1)
        {
            throw new InvalidOperationException(
                $"The output consists of {document.OutputCount} buffers and cannot be written to a single stream. " +
                $"Use {nameof(CompileToDocument)} and write each buffer individually.");
        }
    }

    /// <summary>
    /// Builds the output path for one rendered buffer. Single-buffer output keeps the requested
    /// name; per-page output gets a one-based page number appended.
    /// Input:  "out/label.png", buffer index 1 of 3
    /// Output: "out/label-2.png"
    /// </summary>
    private static string GetOutputPath(string outputFile, int output, int outputCount)
    {
        if (outputCount == 1)
        {
            return outputFile;
        }

        var extension = Path.GetExtension(outputFile);
        var fileName = Path.GetFileNameWithoutExtension(outputFile);
        var directory = Path.GetDirectoryName(outputFile) ?? "";
        return Path.Combine(directory, $"{fileName}-{output + 1}{extension}");
    }

    /// <summary>
    /// Sets the system inputs for the Typst compiler, which are accessible within the Typst script via `sys.inputs`.
    /// </summary>
    /// <param name="inputs">A dictionary of key-value pairs. Values are serialized to JSON and passed to the compiler.</param>
    /// <exception cref="Exception">Thrown if the inputs fail to be set in the native compiler.</exception>
    public unsafe void SetSysInputs(Dictionary<string, string> inputs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TypstCompiler));

        var sysInputsJson = JsonSerializer.Serialize<Dictionary<string, string>>(inputs, sourceGenOptions);
        var sysInputsPtr = Marshal.StringToCoTaskMemUTF8(sysInputsJson);
        try
        {
            var ok = CsBindgen.NativeMethods.set_sys_inputs(_compiler, (byte*)sysInputsPtr);
            if (!ok)
            {
                throw new Exception("Failed to set system inputs");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(sysInputsPtr);
        }
    }


    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual unsafe void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (_compiler != null)
        {
            CsBindgen.NativeMethods.free_compiler(_compiler);
            _compiler = null;
        }

        _disposed = true;
    }

    ~TypstCompiler()
    {
        Dispose(false);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TypstCompiler));
        }
    }
}

public sealed record CompileOutcome(IReadOnlyList<byte[]> Buffers, IReadOnlyList<string> Warnings);
public sealed record AllocationSnapshot(ulong BufferCount, ulong BufferBytes, ulong WarningCount, ulong WarningBytes);
