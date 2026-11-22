using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;

namespace typstsharp;

/// <summary>
/// A thread-safe, bounded pool of <see cref="TypstCompiler"/> instances designed for high-concurrency
/// server workloads such as ASP.NET Core.
/// </summary>
/// <remarks>
/// <para>
/// A single <see cref="TypstCompiler"/> is not thread-safe, and running an unbounded number of native
/// compilations concurrently under a traffic spike can saturate the thread pool and multiply peak
/// memory consumption. <see cref="TypstCompilerPool"/> solves both:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Limits concurrent compilations to <see cref="MaxConcurrency"/> (defaulting to <see cref="Environment.ProcessorCount"/>),
/// queuing excess callers either synchronously or asynchronously without starving the .NET thread pool.
/// </description></item>
/// <item><description>
/// Lazily creates and reuses <see cref="TypstCompiler"/> instances up to <see cref="MaxConcurrency"/>,
/// keeping parsed template syntax trees, imported files, and file-identity keys warm across requests.
/// </description></item>
/// <item><description>
/// Accepts per-compilation <c>sys.inputs</c> (or updated source text) atomically inside each rent-compile-return
/// cycle, and returns the compiler to the pool as soon as <see cref="TypstDocument"/> is produced—before
/// the caller streams the output over the network or to disk.
/// </description></item>
/// </list>
/// </remarks>
public sealed class TypstCompilerPool : IDisposable
{
    private readonly Func<TypstCompiler> _factory;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentBag<TypstCompiler> _idle = [];
    private readonly ConcurrentBag<TypstCompiler> _all = [];
    private readonly int _maxConcurrency;
    private int _disposed;

    /// <summary>
    /// Initializes a new <see cref="TypstCompilerPool"/> using a custom compiler factory.
    /// </summary>
    /// <param name="compilerFactory">Factory invoked to create each pooled <see cref="TypstCompiler"/> instance.</param>
    /// <param name="maxConcurrency">
    /// Maximum number of concurrent compilations and pooled compiler instances. Defaults to
    /// <see cref="Environment.ProcessorCount"/> when <c>0</c> or omitted.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="compilerFactory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxConcurrency"/> is negative.</exception>
    public TypstCompilerPool(Func<TypstCompiler> compilerFactory, int maxConcurrency = 0)
    {
        ArgumentNullException.ThrowIfNull(compilerFactory);
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcurrency);

        _maxConcurrency = maxConcurrency == 0 ? Math.Max(1, Environment.ProcessorCount) : maxConcurrency;
        _factory = compilerFactory;
        _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    /// <summary>
    /// The maximum number of concurrent compilations allowed by this pool.
    /// </summary>
    public int MaxConcurrency => _maxConcurrency;

    /// <summary>
    /// Creates a <see cref="TypstCompilerPool"/> for an on-disk Typst template file.
    /// </summary>
    /// <param name="path">The path to the Typst file.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">Initial system inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <param name="maxConcurrency">Maximum number of concurrent compilations (defaults to <see cref="Environment.ProcessorCount"/>).</param>
    public static TypstCompilerPool FromFile(
        string path,
        Fonts? fonts = null,
        IDictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true,
        int maxConcurrency = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new TypstCompilerPool(
            () => TypstCompiler.FromFile(path, fonts, sysInputs, root, packagePath, includeSystemPackages),
            maxConcurrency);
    }

    /// <summary>
    /// Creates a <see cref="TypstCompilerPool"/> for an in-memory Typst template string.
    /// </summary>
    /// <param name="source">The initial Typst source code.</param>
    /// <param name="fonts">Font settings.</param>
    /// <param name="sysInputs">Initial system inputs.</param>
    /// <param name="root">Root directory.</param>
    /// <param name="packagePath">Directory that packages are resolved from.</param>
    /// <param name="includeSystemPackages">Whether machine-wide package directories and Typst Universe registry may be used.</param>
    /// <param name="maxConcurrency">Maximum number of concurrent compilations (defaults to <see cref="Environment.ProcessorCount"/>).</param>
    public static TypstCompilerPool FromSource(
        string source = "",
        Fonts? fonts = null,
        IDictionary<string, string>? sysInputs = null,
        string? root = null,
        string? packagePath = null,
        bool includeSystemPackages = true,
        int maxConcurrency = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new TypstCompilerPool(
            () => TypstCompiler.FromSource(source, fonts, sysInputs, root, packagePath, includeSystemPackages),
            maxConcurrency);
    }

    /// <summary>
    /// Compiles the template into a zero-copy <see cref="TypstDocument"/>, returning the rented
    /// <see cref="TypstCompiler"/> to the pool immediately so other threads can compile while the
    /// caller reads or streams the returned document.
    /// </summary>
    /// <param name="sysInputs">Optional <c>sys.inputs</c> dictionary to set before compiling.</param>
    /// <param name="format">The output format: "pdf", "png", "svg", or "svg-merged".</param>
    /// <param name="ppi">The pixels per inch used for raster output.</param>
    /// <param name="pdfStandards">Optional PDF standards (e.g. "a-2b", "v-1.7").</param>
    /// <param name="mergedGap">The distance between neighbouring pages in a merged SVG, in points.</param>
    /// <returns>A <see cref="TypstDocument"/> that owns its native memory and must be disposed by the caller.</returns>
    public TypstDocument CompileToDocument(
        IDictionary<string, string>? sysInputs = null,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f)
    {
        return Execute(compiler =>
        {
            if (sysInputs is not null)
            {
                compiler.SetSysInputs(sysInputs);
            }
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        });
    }

    /// <summary>
    /// Asynchronously waits for an available compiler from the pool and compiles the template into
    /// a zero-copy <see cref="TypstDocument"/>.
    /// </summary>
    public ValueTask<TypstDocument> CompileToDocumentAsync(
        IDictionary<string, string>? sysInputs = null,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(compiler =>
        {
            if (sysInputs is not null)
            {
                compiler.SetSysInputs(sysInputs);
            }
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        }, cancellationToken);
    }

    /// <summary>
    /// Compiles the template with a raw JSON object string as <c>sys.inputs</c> into a zero-copy
    /// <see cref="TypstDocument"/>.
    /// </summary>
    public TypstDocument CompileToDocumentWithJsonInputs(
        string sysInputsJson,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f)
    {
        ArgumentNullException.ThrowIfNull(sysInputsJson);
        return Execute(compiler =>
        {
            compiler.SetSysInputsJson(sysInputsJson);
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        });
    }

    /// <summary>
    /// Asynchronously waits for an available compiler from the pool and compiles the template with
    /// a raw JSON object string as <c>sys.inputs</c> into a zero-copy <see cref="TypstDocument"/>.
    /// </summary>
    public ValueTask<TypstDocument> CompileToDocumentWithJsonInputsAsync(
        string sysInputsJson,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sysInputsJson);
        return ExecuteAsync(compiler =>
        {
            compiler.SetSysInputsJson(sysInputsJson);
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        }, cancellationToken);
    }

    /// <summary>
    /// Compiles the template with a strongly-typed <c>sys.inputs</c> value serialized via
    /// <paramref name="jsonTypeInfo"/> into a zero-copy <see cref="TypstDocument"/>.
    /// </summary>
    public TypstDocument CompileToDocument<T>(
        T sysInputs,
        JsonTypeInfo<T> jsonTypeInfo,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f)
    {
        ArgumentNullException.ThrowIfNull(sysInputs);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return Execute(compiler =>
        {
            compiler.SetSysInputs(sysInputs, jsonTypeInfo);
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        });
    }

    /// <summary>
    /// Asynchronously waits for an available compiler from the pool and compiles the template with
    /// a strongly-typed <c>sys.inputs</c> value serialized via <paramref name="jsonTypeInfo"/>
    /// into a zero-copy <see cref="TypstDocument"/>.
    /// </summary>
    public ValueTask<TypstDocument> CompileToDocumentAsync<T>(
        T sysInputs,
        JsonTypeInfo<T> jsonTypeInfo,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sysInputs);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return ExecuteAsync(compiler =>
        {
            compiler.SetSysInputs(sysInputs, jsonTypeInfo);
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        }, cancellationToken);
    }

    /// <summary>
    /// Updates the in-memory source on a pooled compiler (preserving its file identity for
    /// incremental parsing) and compiles it into a zero-copy <see cref="TypstDocument"/>.
    /// </summary>
    public TypstDocument CompileSourceToDocument(
        string source,
        IDictionary<string, string>? sysInputs = null,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Execute(compiler =>
        {
            compiler.SetSource(source);
            if (sysInputs is not null)
            {
                compiler.SetSysInputs(sysInputs);
            }
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        });
    }

    /// <summary>
    /// Asynchronously waits for a pooled compiler, updates its in-memory source, and compiles it
    /// into a zero-copy <see cref="TypstDocument"/>.
    /// </summary>
    public ValueTask<TypstDocument> CompileSourceToDocumentAsync(
        string source,
        IDictionary<string, string>? sysInputs = null,
        string format = "pdf",
        float ppi = 144.0f,
        IEnumerable<string>? pdfStandards = null,
        float mergedGap = 0f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ExecuteAsync(compiler =>
        {
            compiler.SetSource(source);
            if (sysInputs is not null)
            {
                compiler.SetSysInputs(sysInputs);
            }
            return compiler.CompileToDocument(format, ppi, pdfStandards, mergedGap);
        }, cancellationToken);
    }

    /// <summary>
    /// Compiles the template to a <see cref="PdfResult"/>.
    /// </summary>
    public PdfResult CompilePdf(
        IDictionary<string, string>? sysInputs = null,
        IEnumerable<string>? pdfStandards = null)
    {
        using var document = CompileToDocument(sysInputs, format: "pdf", pdfStandards: pdfStandards);
        return new PdfResult(document.GetOutputBytes(), document.Warnings);
    }

    /// <summary>
    /// Compiles the template to a PDF and writes it straight to <paramref name="destination"/>
    /// without buffering the PDF on the managed heap. The pooled compiler is released back to the
    /// pool before the stream write begins.
    /// </summary>
    public IReadOnlyList<string> CompilePdf(
        Stream destination,
        IDictionary<string, string>? sysInputs = null,
        IEnumerable<string>? pdfStandards = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var document = CompileToDocument(sysInputs, format: "pdf", pdfStandards: pdfStandards);
        document.CopyOutputTo(destination);
        return document.Warnings;
    }

    /// <summary>
    /// Asynchronously waits for a pooled compiler, compiles the template to a PDF, releases the
    /// compiler back to the pool immediately, and streams the native output to
    /// <paramref name="destination"/> without buffering on the managed heap.
    /// </summary>
    public async Task<IReadOnlyList<string>> CompilePdfAsync(
        Stream destination,
        IDictionary<string, string>? sysInputs = null,
        IEnumerable<string>? pdfStandards = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var document = await CompileToDocumentAsync(sysInputs, format: "pdf", pdfStandards: pdfStandards, cancellationToken: cancellationToken).ConfigureAwait(false);
        await document.CopyOutputToAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.Warnings;
    }

    /// <summary>
    /// Compiles the template to SVG pages.
    /// </summary>
    public SvgResult CompileSvg(
        IDictionary<string, string>? sysInputs = null,
        float ppi = 144.0f)
    {
        using var document = CompileToDocument(sysInputs, format: "svg", ppi: ppi);
        var pages = new List<string>(document.OutputCount);
        for (int i = 0; i < document.OutputCount; i++)
        {
            pages.Add(System.Text.Encoding.UTF8.GetString(document.GetOutputSpan(i)));
        }
        return new SvgResult(pages, document.Warnings);
    }

    /// <summary>
    /// Compiles the template to a single merged SVG holding every page.
    /// </summary>
    public SvgResult CompileMergedSvg(
        IDictionary<string, string>? sysInputs = null,
        float gapInPoints = 0f)
    {
        using var document = CompileToDocument(sysInputs, format: "svg-merged", mergedGap: gapInPoints);
        var svg = System.Text.Encoding.UTF8.GetString(document.GetOutputSpan());
        return new SvgResult([svg], document.Warnings);
    }

    /// <summary>
    /// Compiles the template to PNG pages.
    /// </summary>
    public PngResult CompilePng(
        IDictionary<string, string>? sysInputs = null,
        float ppi = 144.0f)
    {
        using var document = CompileToDocument(sysInputs, format: "png", ppi: ppi);
        var pages = new List<byte[]>(document.OutputCount);
        for (int i = 0; i < document.OutputCount; i++)
        {
            pages.Add(document.GetOutputBytes(i));
        }
        return new PngResult(pages, document.Warnings);
    }

    /// <summary>
    /// Rents a <see cref="TypstCompiler"/> from the pool, runs <paramref name="action"/> with
    /// exclusive access to it, and returns the compiler to the pool.
    /// </summary>
    public TResult Execute<TResult>(Func<TypstCompiler, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureNotDisposed();

        _semaphore.Wait();
        TypstCompiler? compiler = null;
        try
        {
            EnsureNotDisposed();
            compiler = AcquireCompiler();
            return action(compiler);
        }
        finally
        {
            ReleaseCompiler(compiler);
        }
    }

    /// <summary>
    /// Asynchronously waits to rent a <see cref="TypstCompiler"/> from the pool, runs
    /// <paramref name="action"/> with exclusive access to it, and returns the compiler to the pool.
    /// </summary>
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        Func<TypstCompiler, TResult> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureNotDisposed();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        TypstCompiler? compiler = null;
        try
        {
            EnsureNotDisposed();
            compiler = AcquireCompiler();
            return action(compiler);
        }
        finally
        {
            ReleaseCompiler(compiler);
        }
    }

    private TypstCompiler AcquireCompiler()
    {
        if (_idle.TryTake(out var existing))
        {
            return existing;
        }

        var created = _factory();
        _all.Add(created);
        return created;
    }

    private void ReleaseCompiler(TypstCompiler? compiler)
    {
        try
        {
            if (compiler is not null)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _idle.Add(compiler);
                    if (Volatile.Read(ref _disposed) != 0 && _idle.TryTake(out var late))
                    {
                        late.Dispose();
                    }
                }
                else
                {
                    compiler.Dispose();
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>
    /// Disposes all idle <see cref="TypstCompiler"/> instances in the pool. Any compilers currently
    /// executing a compilation are disposed as soon as their compilation completes.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_idle.TryTake(out var compiler))
        {
            compiler.Dispose();
        }
    }
}
