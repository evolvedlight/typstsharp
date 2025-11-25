using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace typstsharp;

public record Fonts(
    bool IncludeSystemFonts = true,
    IEnumerable<string>? FontPaths = null
);

public unsafe class TypstCompiler : IDisposable
{
    public static string EmptyDictionaryJson => "{}";
    private CsBindgen.Compiler* _compiler;
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
    public TypstCompiler(string inputPath, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null)
        : this(inputPath, null, fonts, sysInputs, root)
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
    public static TypstCompiler FromSource(string source, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null)
    {
        return new TypstCompiler(null, source, fonts, sysInputs, root);
    }

    /// <summary>
    /// Creates a new <see cref="TypstCompiler"/> from a file path.
    /// </summary>
    /// <param name="path">The path to the Typst file.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">System inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <returns>A new <see cref="TypstCompiler"/> instance.</returns>
    public static TypstCompiler FromFile(string path, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null)
    {
        return new TypstCompiler(path, null, fonts, sysInputs, root);
    }

    

    private TypstCompiler(string? inputPath, string? inputSource, Fonts? fonts, Dictionary<string, string>? sysInputs, string? root)
    {
        fonts ??= new Fonts();
        var fontPaths = fonts.FontPaths ?? Enumerable.Empty<string>();
        bool ignoreSystemFonts = !fonts.IncludeSystemFonts;

        var inputPathPtr = inputPath != null ? Marshal.StringToHGlobalAnsi(inputPath) : IntPtr.Zero;
        var inputSourcePtr = inputSource != null ? Marshal.StringToHGlobalAnsi(inputSource) : IntPtr.Zero;
        
        IntPtr rootPtr = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(root))
        {
            rootPtr = Marshal.StringToHGlobalAnsi(root);
        }

        var fontPathsList = fontPaths.ToList();
        var fontPathPtrs = new IntPtr[fontPathsList.Count];
        for (int i = 0; i < fontPathsList.Count; i++)
        {
            fontPathPtrs[i] = Marshal.StringToHGlobalAnsi(fontPathsList[i]);
        }

        var sysInputsJson = sysInputs == null ? "{}" : JsonSerializer.Serialize<Dictionary<string, string>>(sysInputs, sourceGenOptions);
        var sysInputsPtr = Marshal.StringToHGlobalAnsi(sysInputsJson);

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
            if (rootPtr != IntPtr.Zero) Marshal.FreeHGlobal(rootPtr);
            if (inputPathPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPathPtr);
            if (inputSourcePtr != IntPtr.Zero) Marshal.FreeHGlobal(inputSourcePtr);
            foreach (var ptr in fontPathPtrs) Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(sysInputsPtr);
        }
    }

    /// <summary>
    /// Compiles the Typst document.
    /// </summary>
    /// <returns>A <see cref="CompileOutcome"/> containing the compiled document buffers and any warnings.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the compilation fails, with the error message from Typst.</exception>
    public CompileOutcome Compile()
    {
        EnsureNotDisposed();

        var native = CsBindgen.NativeMethods.compile(_compiler);
        try
        {
            if (native.error != null)
            {
                var error = Marshal.PtrToStringAnsi((nint)native.error) ?? "Unknown Typst error";
                throw new InvalidOperationException(error);
            }

            var managedBuffers = new List<byte[]>((int)native.buffers_len);
            if (native.buffers != null)
            {
                for (nuint i = 0; i < native.buffers_len; i++)
                {
                    var buffer = native.buffers[i];
                    var managed = new byte[checked((int)buffer.len)];
                    if (buffer.len > 0 && buffer.ptr != null)
                    {
                        Marshal.Copy((IntPtr)buffer.ptr, managed, 0, managed.Length);
                    }
                    managedBuffers.Add(managed);
                }
            }

            var managedWarnings = new List<string>((int)native.warnings_len);
            if (native.warnings != null)
            {
                for (nuint i = 0; i < native.warnings_len; i++)
                {
                    var warning = native.warnings[i];
                    managedWarnings.Add(Marshal.PtrToStringAnsi((nint)warning.message) ?? string.Empty);
                }
            }

            return new CompileOutcome(managedBuffers, managedWarnings);
        }
        finally
        {
            CsBindgen.NativeMethods.free_compile_result(native);
            CsBindgen.NativeMethods.reset_world();
        }
    }

    public record TypstWarning(string Message);

    /// <summary>
    /// Compiles the Typst document with the specified format and resolution.
    /// </summary>
    /// <param name="format">The output format (e.g., "pdf"). This parameter is currently not used by the underlying engine but is kept for future compatibility.</param>
    /// <param name="ppi">The pixels per inch for the output. This parameter is currently not used by the underlying engine but is kept for future compatibility.</param>
    /// <returns>A tuple containing a list of byte arrays for each page and a list of warnings.</returns>
    public (List<byte[]> pages, List<TypstWarning> warnings) Compile(string format = "pdf", float ppi = 144.0f)
    {
        var outcome = Compile();
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
    public void Compile(string outputFile, string format, float ppi = 144.0f)
    {
        var (pages, _) = Compile(format, ppi);
        if (pages.Count == 1)
        {
            File.WriteAllBytes(outputFile, pages[0]);
        }
        else
        {
            var extension = Path.GetExtension(outputFile);
            var fileName = Path.GetFileNameWithoutExtension(outputFile);
            var directory = Path.GetDirectoryName(outputFile) ?? "";

            for (int i = 0; i < pages.Count; i++)
            {
                var pagePath = Path.Combine(directory, $"{fileName}-{i + 1}{extension}");
                File.WriteAllBytes(pagePath, pages[i]);
            }
        }
    }

    /// <summary>
    /// Sets the system inputs for the Typst compiler, which are accessible within the Typst script via `sys.inputs`.
    /// </summary>
    /// <param name="inputs">A dictionary of key-value pairs. Values are serialized to JSON and passed to the compiler.</param>
    /// <exception cref="Exception">Thrown if the inputs fail to be set in the native compiler.</exception>
    public void SetSysInputs(Dictionary<string, string> inputs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TypstCompiler));

        var sysInputsJson = JsonSerializer.Serialize<Dictionary<string, string>>(inputs, sourceGenOptions);
        var sysInputsPtr = Marshal.StringToHGlobalAnsi(sysInputsJson);
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
            Marshal.FreeHGlobal(sysInputsPtr);
        }
    }


    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
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
