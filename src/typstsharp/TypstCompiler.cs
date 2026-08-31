using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace typstsharp;

public record Fonts(
    bool IncludeSystemFonts = true,
    IEnumerable<string>? FontPaths = null
);

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
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from, in place of the machine-wide package directory.</param>
    /// <param name="includeSystemPackages">
    /// Whether the machine-wide package directories and the Typst Universe registry may be used.
    /// Pass <c>false</c> together with <paramref name="packagePath"/> to resolve packages only from
    /// that directory, which keeps compilation off the network.
    /// </param>
    /// <exception cref="Exception">Thrown when the Typst compiler fails to initialize.</exception>
    public TypstCompiler(string inputPath, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null, string? packagePath = null, bool includeSystemPackages = true)
        : this(inputPath, null, fonts, sysInputs, root, packagePath, includeSystemPackages)
    {
    }

    /// <summary>
    /// Creates a new <see cref="TypstCompiler"/> from a source string.
    /// </summary>
    /// <param name="source">The Typst source code.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from, in place of the machine-wide package directory.</param>
    /// <param name="includeSystemPackages">
    /// Whether the machine-wide package directories and the Typst Universe registry may be used.
    /// Pass <c>false</c> together with <paramref name="packagePath"/> to resolve packages only from
    /// that directory, which keeps compilation off the network.
    /// </param>
    /// <returns>A new <see cref="TypstCompiler"/> instance.</returns>
    public static TypstCompiler FromSource(string source, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null, string? packagePath = null, bool includeSystemPackages = true)
    {
        return new TypstCompiler(null, source, fonts, sysInputs, root, packagePath, includeSystemPackages);
    }

    /// <summary>
    /// Creates a new <see cref="TypstCompiler"/> from a file path.
    /// </summary>
    /// <param name="path">The path to the Typst file.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from, in place of the machine-wide package directory.</param>
    /// <param name="includeSystemPackages">
    /// Whether the machine-wide package directories and the Typst Universe registry may be used.
    /// Pass <c>false</c> together with <paramref name="packagePath"/> to resolve packages only from
    /// that directory, which keeps compilation off the network.
    /// </param>
    /// <returns>A new <see cref="TypstCompiler"/> instance.</returns>
    public static TypstCompiler FromFile(string path, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null, string? packagePath = null, bool includeSystemPackages = true)
    {
        return new TypstCompiler(path, null, fonts, sysInputs, root, packagePath, includeSystemPackages);
    }

    /// <summary>
    /// Compiles Typst source code directly to a PDF.
    /// </summary>
    /// <param name="source">The Typst source code.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <returns>A <see cref="PdfResult"/> containing the PDF bytes and any warnings.</returns>
    public static PdfResult CompilePdf(
        string source,
        Fonts? fonts = null,
        Dictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true,
        IEnumerable<string>? pdfStandards = null)
    {
        using var compiler = FromSource(source, fonts, sysInputs, root, packagePath, includeSystemPackages);
        return compiler.CompilePdf(pdfStandards);
    }

    /// <summary>
    /// Compiles a Typst source file directly to a PDF.
    /// </summary>
    /// <param name="path">The path to the Typst file.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <returns>A <see cref="PdfResult"/> containing the PDF bytes and any warnings.</returns>
    public static PdfResult CompilePdfFromFile(
        string path,
        Fonts? fonts = null,
        Dictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true,
        IEnumerable<string>? pdfStandards = null)
    {
        using var compiler = FromFile(path, fonts, sysInputs, root, packagePath, includeSystemPackages);
        return compiler.CompilePdf(pdfStandards);
    }

    /// <summary>
    /// Compiles Typst source code directly to SVG format.
    /// </summary>
    /// <param name="source">The Typst source code.</param>
    /// <param name="ppi">The pixels per inch for raster assets in the SVG.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <returns>A <see cref="SvgResult"/> containing SVG string per page and any warnings.</returns>
    public static SvgResult CompileSvg(
        string source,
        float ppi = 144.0f,
        Fonts? fonts = null,
        Dictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true)
    {
        using var compiler = FromSource(source, fonts, sysInputs, root, packagePath, includeSystemPackages);
        return compiler.CompileSvg(ppi);
    }

    /// <summary>
    /// Compiles a Typst source file directly to SVG format.
    /// </summary>
    /// <param name="path">The path to the Typst file.</param>
    /// <param name="ppi">The pixels per inch for raster assets in the SVG.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <returns>A <see cref="SvgResult"/> containing SVG string per page and any warnings.</returns>
    public static SvgResult CompileSvgFromFile(
        string path,
        float ppi = 144.0f,
        Fonts? fonts = null,
        Dictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true)
    {
        using var compiler = FromFile(path, fonts, sysInputs, root, packagePath, includeSystemPackages);
        return compiler.CompileSvg(ppi);
    }

    /// <summary>
    /// Compiles Typst source code directly to PNG format.
    /// </summary>
    /// <param name="source">The Typst source code.</param>
    /// <param name="ppi">The pixels per inch resolution for the PNG export.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <returns>A <see cref="PngResult"/> containing PNG bytes per page and any warnings.</returns>
    public static PngResult CompilePng(
        string source,
        float ppi = 144.0f,
        Fonts? fonts = null,
        Dictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true)
    {
        using var compiler = FromSource(source, fonts, sysInputs, root, packagePath, includeSystemPackages);
        return compiler.CompilePng(ppi);
    }

    /// <summary>
    /// Compiles a Typst source file directly to PNG format.
    /// </summary>
    /// <param name="path">The path to the Typst file.</param>
    /// <param name="ppi">The pixels per inch resolution for the PNG export.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <returns>A <see cref="PngResult"/> containing PNG bytes per page and any warnings.</returns>
    public static PngResult CompilePngFromFile(
        string path,
        float ppi = 144.0f,
        Fonts? fonts = null,
        Dictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true)
    {
        using var compiler = FromFile(path, fonts, sysInputs, root, packagePath, includeSystemPackages);
        return compiler.CompilePng(ppi);
    }

    

    private unsafe TypstCompiler(string? inputPath, string? inputSource, Fonts? fonts, Dictionary<string, string>? sysInputs, string? root, string? packagePath = null, bool includeSystemPackages = true)
    {
        fonts ??= new Fonts();
        var fontPaths = fonts.FontPaths ?? [];
        bool ignoreSystemFonts = !fonts.IncludeSystemFonts;
        bool ignoreSystemPackages = !includeSystemPackages;

        if (string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(inputPath) && Path.IsPathRooted(inputPath))
        {
            root = Path.GetDirectoryName(inputPath);
        }

        var inputPathPtr = inputPath != null ? Marshal.StringToCoTaskMemUTF8(inputPath) : IntPtr.Zero;

        // The source goes over as raw UTF-8 bytes with an explicit length. A Typst
        // document may contain NUL bytes, and a NUL-terminated string would be
        // silently truncated at the first one.
        byte[]? inputSourceBytes = null;
        nuint inputSourceLen = 0;
        if (inputSource != null)
        {
            var encoded = Encoding.UTF8.GetBytes(inputSource);
            inputSourceLen = (nuint)encoded.Length;
            // `fixed` over an empty array yields a null pointer, which the native
            // side reads as "no source at all". A one-byte placeholder keeps an
            // empty document distinguishable; the length passed stays 0.
            inputSourceBytes = encoded.Length == 0 ? new byte[1] : encoded;
        }

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
            fixed (byte* inputSourcePtr = inputSourceBytes)
            {
                IntPtr* fontPathsPtr = fontPathsList.Count == 0 ? null : fontPathsRawPtr;
                _compiler = CsBindgen.NativeMethods.create_compiler(
                    (byte*)rootPtr,
                    (byte*)inputPathPtr,
                    inputSourcePtr,
                    inputSourceLen,
                    (byte**)fontPathsPtr,
                    (nuint)fontPathsList.Count,
                    (byte*)packagePathPtr,
                    (byte*)sysInputsPtr,
                    ignoreSystemFonts,
                    ignoreSystemPackages);
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
            foreach (var ptr in fontPathPtrs) Marshal.FreeCoTaskMem(ptr);
            if (packagePathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(packagePathPtr);
            Marshal.FreeCoTaskMem(sysInputsPtr);
        }
    }

    /// <summary>
    /// Compiles the Typst document and hands back the rendered output while it is still in the memory
    /// the native library allocated, without copying it onto the managed heap. The caller decides
    /// whether the bytes are ever copied.
    /// </summary>
    /// <param name="format">The output format: "pdf", "png" or "svg".</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
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
            // compiling. `this` is dead from the field load above onwards, so without this the
            // finalizer could free the native world underneath the call that is still using it.
            GC.KeepAlive(this);

            try
            {
                if (native.error_ptr != null)
                {
                    var error = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(native.error_ptr, checked((int)native.error_len)));
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
    /// Compiles the Typst document and copies the rendered output onto the managed heap.
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

    /// <summary>
    /// Compiles the Typst document directly to a PDF.
    /// </summary>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <returns>A <see cref="PdfResult"/> containing the PDF bytes and any warnings.</returns>
    public PdfResult CompilePdf(IEnumerable<string>? pdfStandards = null)
    {
        var outcome = Compile(format: "pdf", pdfStandards: pdfStandards);
        return outcome.AsPdf();
    }

    /// <summary>
    /// Compiles the Typst document to a PDF and streams it straight to a file, without
    /// buffering it on the managed heap.
    /// </summary>
    /// <param name="outputFile">The path of the destination PDF file.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <returns>The warnings reported by the Typst compiler.</returns>
    public IReadOnlyList<string> CompilePdf(string outputFile, IEnumerable<string>? pdfStandards = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFile);

        using var document = CompileToDocument("pdf", pdfStandards: pdfStandards);
        document.WriteOutputToFile(outputFile);
        return document.Warnings;
    }

    /// <summary>
    /// Asynchronously compiles the Typst document to a PDF and streams it straight to a file,
    /// without buffering it on the managed heap.
    /// </summary>
    /// <param name="outputFile">The path of the destination PDF file.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The warnings reported by the Typst compiler.</returns>
    public async Task<IReadOnlyList<string>> CompilePdfAsync(string outputFile, IEnumerable<string>? pdfStandards = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFile);

        using var document = CompileToDocument("pdf", pdfStandards: pdfStandards);
        await document.WriteOutputToFileAsync(outputFile, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.Warnings;
    }

    /// <summary>
    /// Compiles the Typst document to a PDF and writes it to a stream, without buffering it on
    /// the managed heap.
    /// </summary>
    /// <param name="destination">The destination stream to write the PDF bytes to.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <returns>The warnings reported by the Typst compiler.</returns>
    public IReadOnlyList<string> CompilePdf(Stream destination, IEnumerable<string>? pdfStandards = null)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var document = CompileToDocument("pdf", pdfStandards: pdfStandards);
        document.CopyOutputTo(destination);
        return document.Warnings;
    }

    /// <summary>
    /// Asynchronously compiles the Typst document to a PDF and writes it to a stream, without
    /// buffering it on the managed heap.
    /// </summary>
    /// <param name="destination">The destination stream to write the PDF bytes to.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The warnings reported by the Typst compiler.</returns>
    public async Task<IReadOnlyList<string>> CompilePdfAsync(Stream destination, IEnumerable<string>? pdfStandards = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var document = CompileToDocument("pdf", pdfStandards: pdfStandards);
        await document.CopyOutputToAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.Warnings;
    }

    /// <summary>
    /// Compiles the Typst document to SVG string pages (one per page).
    /// </summary>
    /// <param name="ppi">The pixels per inch for raster assets in the SVG.</param>
    /// <returns>A <see cref="SvgResult"/> containing SVG string per page and any warnings.</returns>
    public SvgResult CompileSvg(float ppi = 144.0f)
    {
        using var document = CompileToDocument(format: "svg", ppi: ppi);

        var pages = new List<string>(document.OutputCount);
        for (int i = 0; i < document.OutputCount; i++)
        {
            // Decoding straight from native memory skips the intermediate byte[] per page.
            pages.Add(System.Text.Encoding.UTF8.GetString(document.GetOutputSpan(i)));
        }

        return new SvgResult(pages, document.Warnings);
    }

    /// <summary>
    /// Compiles the Typst document to PNG image byte buffers (one per page).
    /// </summary>
    /// <param name="ppi">The pixels per inch resolution for the PNG export.</param>
    /// <returns>A <see cref="PngResult"/> containing PNG bytes per page and any warnings.</returns>
    public PngResult CompilePng(float ppi = 144.0f)
    {
        using var document = CompileToDocument(format: "png", ppi: ppi);

        var pages = new List<byte[]>(document.OutputCount);
        for (int i = 0; i < document.OutputCount; i++)
        {
            pages.Add(document.GetOutputBytes(i));
        }

        return new PngResult(pages, document.Warnings);
    }

    public record TypstWarning(string Message);

    /// <summary>
    /// Compiles the Typst document with the specified format and resolution.
    /// </summary>
    /// <param name="format">The output format (e.g., "pdf"). This parameter is currently not used by the underlying engine but is kept for future compatibility.</param>
    /// <param name="ppi">The pixels per inch for the output. This parameter is currently not used by the underlying engine but is kept for future compatibility.</param>
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
    /// Compiles the Typst document and writes the output to one or more files.
    /// </summary>
    /// <param name="outputFile">The path for the output file. If the document has multiple pages, a page number will be appended to the file name for each page.</param>
    /// <param name="format">The output format (e.g., "pdf"). This parameter is currently not used by the underlying engine but is kept for future compatibility.</param>
    /// <param name="ppi">The pixels per inch for the output. This parameter is currently not used by the underlying engine but is kept for future compatibility.</param>
    public void Compile(string outputFile, string format, float ppi = 144.0f, IEnumerable<string>? pdfStandards = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFile);

        using var document = CompileToDocument(format, ppi, pdfStandards);
        if (document.OutputCount == 1)
        {
            document.WriteOutputToFile(outputFile);
            return;
        }

        // A format that renders one buffer per page gets a page number appended, so "out.png"
        // becomes "out-1.png", "out-2.png" and so on.
        var directory = Path.GetDirectoryName(outputFile) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(outputFile);
        var extension = Path.GetExtension(outputFile);

        for (int i = 0; i < document.OutputCount; i++)
        {
            document.WriteOutputToFile(Path.Combine(directory, $"{fileName}-{i + 1}{extension}"), i);
        }
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
            GC.KeepAlive(this);

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

#pragma warning disable CS0618
public sealed record CompileOutcome(
    [property: Obsolete("Buffers is deprecated and will be removed in a future version. For PDF export, prefer 'compiler.CompilePdf()' or 'outcome.AsPdf()'. For SVG/PNG, prefer 'compiler.CompileSvg()' or 'compiler.CompilePng()'.")]
    IReadOnlyList<byte[]> Buffers, 
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Returns the primary buffer as a <see cref="PdfResult"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no buffers were produced.</exception>
    public PdfResult AsPdf()
    {
        if (Buffers.Count == 0)
        {
            throw new InvalidOperationException("No buffers were produced during compilation.");
        }
        return new PdfResult(Buffers[0], Warnings);
    }

    /// <summary>
    /// Gets the primary document buffer.
    /// </summary>
    public byte[] PrimaryBuffer => Buffers.Count > 0
        ? Buffers[0]
        : throw new InvalidOperationException("No buffers were produced during compilation.");
}
#pragma warning restore CS0618

/// <summary>
/// Represents the result of compiling a document to PDF format.
/// Supports implicit conversion to <see cref="byte[]"/>, <see cref="ReadOnlySpan{T}"/>, and <see cref="ReadOnlyMemory{T}"/>.
/// </summary>
public sealed record PdfResult(byte[] Bytes, IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Gets the length of the PDF byte buffer.
    /// </summary>
    public int Length => Bytes.Length;

    public static implicit operator byte[](PdfResult result) => result.Bytes;
    public static implicit operator ReadOnlySpan<byte>(PdfResult result) => result.Bytes;
    public static implicit operator ReadOnlyMemory<byte>(PdfResult result) => result.Bytes;

    /// <summary>
    /// Creates a readable <see cref="MemoryStream"/> over the PDF bytes.
    /// </summary>
    public MemoryStream ToStream() => new(Bytes);

    /// <summary>
    /// Saves the PDF bytes to a file.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    public void Save(string path) => File.WriteAllBytes(path, Bytes);

    /// <summary>
    /// Saves the PDF bytes to a file asynchronously.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SaveAsync(string path, CancellationToken cancellationToken = default) =>
        File.WriteAllBytesAsync(path, Bytes, cancellationToken);
}

/// <summary>
/// Represents the result of compiling a document to SVG format (one SVG string per page).
/// Supports implicit conversion to <see cref="string"/> (returning the primary page SVG).
/// </summary>
public sealed record SvgResult(IReadOnlyList<string> Pages, IReadOnlyList<string> Warnings) : IReadOnlyList<string>
{
    public int Count => Pages.Count;
    public string this[int index] => Pages[index];
    public IEnumerator<string> GetEnumerator() => Pages.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Pages.GetEnumerator();

    /// <summary>
    /// Implicitly converts the <see cref="SvgResult"/> to a <see cref="string"/> containing the primary SVG page.
    /// </summary>
    public static implicit operator string(SvgResult result) => result.Pages.Count > 0 ? result.Pages[0] : string.Empty;

    /// <summary>
    /// Gets the single SVG page if exactly one page was generated.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the document does not contain exactly 1 page.</exception>
    public string SinglePage => Pages.Count == 1 
        ? Pages[0] 
        : throw new InvalidOperationException($"Expected exactly 1 SVG page, but document produced {Pages.Count} pages.");

    /// <summary>
    /// Gets the primary (first) SVG page.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no pages were produced.</exception>
    public string PrimaryPage => Pages.Count > 0
        ? Pages[0]
        : throw new InvalidOperationException("No SVG pages were produced during compilation.");

    /// <summary>
    /// Saves the primary SVG page (or specified page index) to a file.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="pageIndex">Zero-based page index (defaults to 0).</param>
    public void Save(string path, int pageIndex = 0)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index {pageIndex} is out of range for document with {Pages.Count} pages.");
        }
        File.WriteAllText(path, Pages[pageIndex]);
    }

    /// <summary>
    /// Saves the primary SVG page (or specified page index) to a file asynchronously.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="pageIndex">Zero-based page index (defaults to 0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SaveAsync(string path, int pageIndex = 0, CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index {pageIndex} is out of range for document with {Pages.Count} pages.");
        }
        return File.WriteAllTextAsync(path, Pages[pageIndex], cancellationToken);
    }
}

/// <summary>
/// Represents the result of compiling a document to PNG format (one PNG image byte array per page).
/// Supports implicit conversion to <see cref="byte[]"/> (returning the primary page PNG bytes).
/// </summary>
public sealed record PngResult(IReadOnlyList<byte[]> Pages, IReadOnlyList<string> Warnings) : IReadOnlyList<byte[]>
{
    public int Count => Pages.Count;
    public byte[] this[int index] => Pages[index];
    public IEnumerator<byte[]> GetEnumerator() => Pages.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Pages.GetEnumerator();

    /// <summary>
    /// Implicitly converts the <see cref="PngResult"/> to <see cref="byte[]"/> of the primary PNG page.
    /// </summary>
    public static implicit operator byte[](PngResult result) => result.Pages.Count > 0 ? result.Pages[0] : [];

    /// <summary>
    /// Gets the single PNG page bytes if exactly one page was generated.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the document does not contain exactly 1 page.</exception>
    public byte[] SinglePage => Pages.Count == 1
        ? Pages[0]
        : throw new InvalidOperationException($"Expected exactly 1 PNG page, but document produced {Pages.Count} pages.");

    /// <summary>
    /// Gets the primary (first) PNG page bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no pages were produced.</exception>
    public byte[] PrimaryPage => Pages.Count > 0
        ? Pages[0]
        : throw new InvalidOperationException("No PNG pages were produced during compilation.");

    /// <summary>
    /// Saves the primary PNG page (or specified page index) to a file.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="pageIndex">Zero-based page index (defaults to 0).</param>
    public void Save(string path, int pageIndex = 0)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index {pageIndex} is out of range for document with {Pages.Count} pages.");
        }
        File.WriteAllBytes(path, Pages[pageIndex]);
    }

    /// <summary>
    /// Saves the primary PNG page (or specified page index) to a file asynchronously.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="pageIndex">Zero-based page index (defaults to 0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SaveAsync(string path, int pageIndex = 0, CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index {pageIndex} is out of range for document with {Pages.Count} pages.");
        }
        return File.WriteAllBytesAsync(path, Pages[pageIndex], cancellationToken);
    }
}

public sealed record AllocationSnapshot(ulong BufferCount, ulong BufferBytes, ulong WarningCount, ulong WarningBytes);
