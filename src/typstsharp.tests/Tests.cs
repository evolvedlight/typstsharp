using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace typstsharp.tests;

public class Tests
{
    [Test]
    public async Task BasicSource()
    {
        var compiler = TypstCompiler.FromSource("Hello World 2");
        var result = compiler.Compile().Buffers[0];
        var plainText = GetPlainText(result);
        await Assert.That(plainText).Contains("World 2");
    }

    [Test]
    public async Task CompilePdfDirect()
    {
        using var compiler = TypstCompiler.FromSource("= Hello Direct PDF");
        byte[] pdf = compiler.CompilePdf();
        var plainText = GetPlainText(pdf);
        await Assert.That(plainText).Contains("Hello Direct PDF");
    }

    [Test]
    public async Task CompilePdfStatic()
    {
        byte[] pdf = TypstCompiler.CompilePdf("= Hello Static PDF");
        var plainText = GetPlainText(pdf);
        await Assert.That(plainText).Contains("Hello Static PDF");
    }

    [Test]
    public async Task CompilePdfStaticFromFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"typst-{Guid.NewGuid():N}.typ");
        try
        {
            await File.WriteAllTextAsync(tempFile, "= Hello From Temp File");
            byte[] pdf = TypstCompiler.CompilePdfFromFile(tempFile);
            var plainText = GetPlainText(pdf);
            await Assert.That(plainText).Contains("Hello From Temp File");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task CompilePdfToFileAndAsync()
    {
        using var compiler = TypstCompiler.FromSource("= Hello PDF To File");
        var tempPdf1 = Path.Combine(Path.GetTempPath(), $"typst-{Guid.NewGuid():N}.pdf");
        var tempPdf2 = Path.Combine(Path.GetTempPath(), $"typst-{Guid.NewGuid():N}.pdf");
        try
        {
            compiler.CompilePdf(tempPdf1);
            await Assert.That(File.Exists(tempPdf1)).IsTrue();
            var text1 = GetPlainText(await File.ReadAllBytesAsync(tempPdf1));
            await Assert.That(text1).Contains("Hello PDF To File");

            await compiler.CompilePdfAsync(tempPdf2);
            await Assert.That(File.Exists(tempPdf2)).IsTrue();
            var text2 = GetPlainText(await File.ReadAllBytesAsync(tempPdf2));
            await Assert.That(text2).Contains("Hello PDF To File");
        }
        finally
        {
            if (File.Exists(tempPdf1)) File.Delete(tempPdf1);
            if (File.Exists(tempPdf2)) File.Delete(tempPdf2);
        }
    }

    [Test]
    public async Task CompilePdfToStreamAndAsync()
    {
        using var compiler = TypstCompiler.FromSource("= Stream Test");
        
        using var ms1 = new MemoryStream();
        compiler.CompilePdf(ms1);
        var text1 = GetPlainText(ms1.ToArray());
        await Assert.That(text1).Contains("Stream Test");

        using var ms2 = new MemoryStream();
        await compiler.CompilePdfAsync(ms2);
        var text2 = GetPlainText(ms2.ToArray());
        await Assert.That(text2).Contains("Stream Test");
    }

    [Test]
    public async Task CompileOutcomeAsPdfAndPrimaryBuffer()
    {
        using var compiler = TypstCompiler.FromSource("= Outcome Helper Test");
        var outcome = compiler.Compile();
        
        byte[] pdf1 = outcome.AsPdf();
        byte[] pdf2 = outcome.PrimaryBuffer;

        await Assert.That(GetPlainText(pdf1)).Contains("Outcome Helper Test");
        await Assert.That(GetPlainText(pdf2)).Contains("Outcome Helper Test");
    }

    [Test]
    public async Task CompileSvgAndPng()
    {
        using var compiler = TypstCompiler.FromSource("= Hello Vector and Raster");
        
        var svg = compiler.CompileSvg();
        await Assert.That(svg.Count).IsEqualTo(1);
        await Assert.That(svg[0]).Contains("<svg");
        await Assert.That(svg.SinglePage).Contains("<svg");
        await Assert.That(svg.PrimaryPage).Contains("<svg");

        // Implicit string conversion for single/primary SVG page
        string svgString = svg;
        await Assert.That(svgString).Contains("<svg");

        var png = compiler.CompilePng();
        await Assert.That(png.Count).IsEqualTo(1);
        await Assert.That(png.SinglePage.Length).IsGreaterThan(8);
        await Assert.That(png.PrimaryPage.Length).IsGreaterThan(8);
        // PNG magic header: 0x89, 0x50, 0x4E, 0x47
        await Assert.That(png[0].Length).IsGreaterThan(8);
        await Assert.That(png[0][0]).IsEqualTo((byte)0x89);
        await Assert.That(png[0][1]).IsEqualTo((byte)0x50);
        await Assert.That(png[0][2]).IsEqualTo((byte)0x4E);
        await Assert.That(png[0][3]).IsEqualTo((byte)0x47);
    }

    [Test]
    public async Task CompileSingleSvgStandaloneFormula()
    {
        // Example of compiling a formula/diagram snippet to a standalone tightly-cropped SVG
        const string snippet = """
            #set page(width: auto, height: auto, margin: 5pt)
            $ integral_0^infinity e^(-x^2) dif x = sqrt(pi)/2 $
            """;

        // One-liner static call returning string via implicit conversion
        string singleSvg = TypstCompiler.CompileSvg(snippet);
        await Assert.That(singleSvg).Contains("<svg");
        await Assert.That(singleSvg).Contains("</svg>");

        // Test saving to file
        var tempSvg = Path.Combine(Path.GetTempPath(), $"typst-{Guid.NewGuid():N}.svg");
        try
        {
            var result = TypstCompiler.CompileSvg(snippet);
            await result.SaveAsync(tempSvg);
            await Assert.That(File.Exists(tempSvg)).IsTrue();
            var content = await File.ReadAllTextAsync(tempSvg);
            await Assert.That(content).Contains("<svg");
        }
        finally
        {
            if (File.Exists(tempSvg)) File.Delete(tempSvg);
        }
    }

    [Test]
    public async Task CompileMultiPageSvgSinglePageThrows()
    {
        // Multi-page document: SinglePage property should throw InvalidOperationException
        const string multiPage = """
            Page 1
            #pagebreak()
            Page 2
            """;

        var result = TypstCompiler.CompileSvg(multiPage);
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(() => _ = result.SinglePage).Throws<InvalidOperationException>();
        await Assert.That(result.PrimaryPage).Contains("<svg");
    }

    [Test]
    public async Task TestUnicode()
    {
        // Reported as #9
        var compiler = TypstCompiler.FromSource("= Hello world’s");
        var result = compiler.Compile().Buffers[0];
        var plainText = GetPlainText(result);
        await Assert.That(plainText).Contains("Hello world’s");
    }

    [Test]
    public async Task BasicException()
    {
        const string source = "This is not a valid document: # what?";
        var regexMatcher = StringMatcher.AsRegex(
            @"^expected expression \(line 1, column \d+: `[^`]*`\)\r?");

        await Assert.That(() =>
            {
                using var compiler = TypstCompiler.FromSource(source);
                _ = compiler.Compile();
            }).Throws<InvalidOperationException>()
            .WithMessageMatching(regexMatcher);
    }

    [Test]
    public async Task BasicExceptionWithTwoErrors()
    {
        const string content = """
                               This is not a valid document: # what?

                               As a test - this should be able to have a second error, as you can't use dollars here: $50
                               """;
        var regexMatcher = StringMatcher.AsRegex(
            @"^expected expression \(line 1, column \d+: `[^`]*`\)\r?\n" +
            @"unclosed delimiter \(line 3, column \d+: `[^`]*`\)$");
        
        await Assert.That(() =>
            {
                using var compiler = TypstCompiler.FromSource(content);
                _ = compiler.Compile();
            }).Throws<InvalidOperationException>()
            .WithMessageMatching(regexMatcher);
    }

    [Test]
    public async Task WarningWithNullByteIsHandledCorrectly()
    {
        using var compiler = TypstCompiler.FromSource("#set text(font: sys.inputs.f)");
        compiler.SetSysInputs(new Dictionary<string, string> { ["f"] = "A\0B" });
        var outcome = compiler.Compile();
        await Assert.That(outcome.Warnings[0]).Contains("a\0b");
    }

    [Test]
    public async Task ErrorWithNullByteIsHandledCorrectly()
    {
        var ex = await Assert.That(() =>
        {
            using var compiler = TypstCompiler.FromSource("#panic(sys.inputs.f)");
            compiler.SetSysInputs(new Dictionary<string, string> { ["f"] = "foo\0bar" });
            _ = compiler.Compile();
        }).Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("foo\0bar");
    }

    [Test]
    public async Task SourceAfterNullByteIsNotTruncated()
    {
        // The source used to cross the boundary as a C string, so everything from
        // the NUL onwards was dropped and only the first heading was rendered.
        using var compiler = TypstCompiler.FromSource("= Before\0\n\n= After");
        var plainText = GetPlainText(compiler.CompilePdf());
        await Assert.That(plainText).Contains("After");
    }

    [Test]
    public async Task InvalidPdfStandardThrowsException()
    {
        await Assert.That(() =>
        {
            using var compiler = TypstCompiler.FromSource("Hello world");
            _ = compiler.Compile(pdfStandards: ["nonexistent-standard"]);
        }).Throws<InvalidOperationException>()
        .WithMessageMatching(StringMatcher.AsRegex(@"^Invalid PDF standard: nonexistent-standard$"));
    }

    /// <summary>
    /// An application that ships its packages next to the binary resolves them from that
    /// folder without the machine-wide package directories or the registry taking part.
    /// </summary>
    [Test]
    public async Task BundledPackageResolvesWithSystemPackagesExcluded()
    {
        using var packages = new PackageDirectory();
        packages.AddPackage("local", "greet", "0.1.0", "#let greet() = [Hello from a bundled package]");

        using var compiler = TypstCompiler.FromSource(
            """
            #import "@local/greet:0.1.0": greet
            #greet()
            """,
            packagePath: packages.Path,
            includeSystemPackages: false);

        var plainText = GetPlainText(compiler.Compile().Buffers[0]);

        await Assert.That(plainText).Contains("Hello from a bundled package");
    }

    /// <summary>
    /// `@preview/example:0.1.0` is published on Typst Universe, so this compiles only if the
    /// registry is reachable. Excluding system packages has to turn it into a hard failure
    /// rather than a download.
    /// </summary>
    [Test]
    public async Task ExcludingSystemPackagesKeepsTheRegistryOutOfReach()
    {
        using var packages = new PackageDirectory();

        await Assert.That(() =>
            {
                using var compiler = TypstCompiler.FromSource(
                    """
                    #import "@preview/example:0.1.0": *
                    #add(1, 2)
                    """,
                    packagePath: packages.Path,
                    includeSystemPackages: false);
                _ = compiler.Compile();
            }).Throws<InvalidOperationException>()
            .WithMessageContaining("package not found");
    }

    /// <summary>
    /// Without a package path there is nowhere left to look once system packages are excluded,
    /// so even a package installed on the machine stays invisible.
    /// </summary>
    [Test]
    public async Task ExcludingSystemPackagesWithoutAPackagePathFindsNothing()
    {
        await Assert.That(() =>
            {
                using var compiler = TypstCompiler.FromSource(
                    """
                    #import "@local/greet:0.1.0": greet
                    #greet()
                    """,
                    includeSystemPackages: false);
                _ = compiler.Compile();
            }).Throws<InvalidOperationException>()
            .WithMessageContaining("package not found");
    }

    [Test]
    public async Task DocumentExposesOutputWithoutCopying()
    {
        using var compiler = TypstCompiler.FromSource("= Zero copy");
        using var document = compiler.CompileToDocument();

        await Assert.That(document.OutputCount).IsEqualTo(1);
        await Assert.That(GetPlainText(document.GetOutputBytes())).Contains("Zero copy");
    }

    [Test]
    public async Task OutputStreamHasSameContentAsOutputBytes()
    {
        using var compiler = TypstCompiler.FromSource("= Streamed");
        using var document = compiler.CompileToDocument();
        var expected = document.GetOutputBytes();

        using var pageStream = document.OpenOutputStream();
        using var copy = new MemoryStream();
        pageStream.CopyTo(copy);

        await Assert.That(pageStream.Length).IsEqualTo((long)expected.Length);
        await Assert.That(copy.ToArray().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task RentedOutputHasExactLengthAndSameContent()
    {
        using var compiler = TypstCompiler.FromSource("= Pooled");
        using var document = compiler.CompileToDocument();
        var expected = document.GetOutputBytes();

        using var rented = document.RentOutput();

        await Assert.That(rented.Memory.Length).IsEqualTo(expected.Length);
        await Assert.That(rented.Memory.ToArray().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task WriteOutputToFileMatchesOutputBytes()
    {
        using var compiler = TypstCompiler.FromSource("= Written directly to disk");
        using var document = compiler.CompileToDocument();
        var expected = document.GetOutputBytes();
        var path = GetTempFilePath(".pdf");

        try
        {
            document.WriteOutputToFile(path);
            var written = await File.ReadAllBytesAsync(path);

            await Assert.That(written.Length).IsEqualTo(expected.Length);
            await Assert.That(written.SequenceEqual(expected)).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task WriteOutputToFileAsyncMatchesOutputBytes()
    {
        using var compiler = TypstCompiler.FromSource("= Written asynchronously");
        using var document = compiler.CompileToDocument();
        var expected = document.GetOutputBytes();
        var path = GetTempFilePath(".pdf");

        try
        {
            await document.WriteOutputToFileAsync(path);
            var written = await File.ReadAllBytesAsync(path);

            await Assert.That(written.Length).IsEqualTo(expected.Length);
            await Assert.That(written.SequenceEqual(expected)).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task CompilingToAFileProducesReadablePdf()
    {
        using var compiler = TypstCompiler.FromSource("= Compiled straight to a file");
        var path = GetTempFilePath(".pdf");

        try
        {
            compiler.Compile(path, "pdf");

            await Assert.That(GetPlainText(await File.ReadAllBytesAsync(path))).Contains("straight to a file");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MultiPageOutputIsWrittenToNumberedFiles()
    {
        using var compiler = TypstCompiler.FromSource(TwoPageSource);
        var directory = CreateTempDirectory();

        try
        {
            compiler.Compile(Path.Combine(directory, "page.png"), "png");

            await Assert.That(File.Exists(Path.Combine(directory, "page-1.png"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(directory, "page-2.png"))).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task DisposedDocumentRejectsOutputAccess()
    {
        using var compiler = TypstCompiler.FromSource("= Disposed");
        var document = compiler.CompileToDocument();
        document.Dispose();

        await Assert.That(() => document.GetOutputBytes()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposingADocumentTwiceIsSafe()
    {
        using var compiler = TypstCompiler.FromSource("= Disposed twice");
        var document = compiler.CompileToDocument();

        document.Dispose();
        document.Dispose();

        await Assert.That(document.OutputCount).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownOutputIndexIsRejected()
    {
        using var compiler = TypstCompiler.FromSource("= Single page");
        using var document = compiler.CompileToDocument();

        await Assert.That(() => document.GetOutputBytes(1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => document.GetOutputBytes(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WarningsRemainReadableAfterDisposal()
    {
        using var compiler = TypstCompiler.FromSource(WarningSource);
        var document = compiler.CompileToDocument();
        document.Dispose();

        await Assert.That(document.Warnings.Count).IsGreaterThan(0);
    }

    // An S3 upload hands the stream to the AWS SDK, which reads its length and rewinds to retry.
    [Test]
    public async Task OutputStreamIsSeekableAndCanBeReRead()
    {
        using var compiler = TypstCompiler.FromSource("= Uploaded");
        using var document = compiler.CompileToDocument();
        var expected = document.GetOutputBytes();

        using var stream = document.OpenOutputStream();
        await Assert.That(stream.CanSeek).IsTrue();
        await Assert.That(stream.Length).IsEqualTo((long)expected.Length);

        var first = new byte[expected.Length];
        stream.ReadExactly(first);

        stream.Seek(0, SeekOrigin.Begin);
        var second = new byte[expected.Length];
        stream.ReadExactly(second);

        await Assert.That(first.SequenceEqual(expected)).IsTrue();
        await Assert.That(second.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task OutputStreamRejectsReadsAfterTheDocumentIsDisposed()
    {
        using var compiler = TypstCompiler.FromSource("= Disposed underneath");
        var document = compiler.CompileToDocument();
        var stream = document.OpenOutputStream();

        document.Dispose();

        await Assert.That(() => stream.ReadByte()).Throws<ObjectDisposedException>();
        await Assert.That(() => stream.Read(new byte[16], 0, 16)).Throws<ObjectDisposedException>();
        await Assert.That(() => stream.Read(new byte[16].AsSpan())).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task OutputLengthMatchesOutputBytes()
    {
        using var compiler = TypstCompiler.FromSource("= Measured");
        using var document = compiler.CompileToDocument();

        await Assert.That(document.GetOutputLength()).IsEqualTo((long)document.GetOutputBytes().Length);
    }

    [Test]
    public async Task CopyOutputToWritesTheWholeBuffer()
    {
        using var compiler = TypstCompiler.FromSource("= Copied");
        using var document = compiler.CompileToDocument();
        var expected = document.GetOutputBytes();

        using var sync = new MemoryStream();
        document.CopyOutputTo(sync);

        using var async = new MemoryStream();
        await document.CopyOutputToAsync(async);

        await Assert.That(sync.ToArray().SequenceEqual(expected)).IsTrue();
        await Assert.That(async.ToArray().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task RentedOutputIsUnusableAfterDisposalAndSafeToDisposeTwice()
    {
        using var compiler = TypstCompiler.FromSource("= Returned to the pool");
        using var document = compiler.CompileToDocument();

        var rented = document.RentOutput();
        rented.Dispose();
        rented.Dispose();

        await Assert.That(() => rented.Memory).Throws<ObjectDisposedException>();
    }

    // The written file must not keep a tail from whatever was there before.
    [Test]
    public async Task WriteOutputToFileTruncatesALargerExistingFile()
    {
        using var compiler = TypstCompiler.FromSource("= Overwritten");
        using var document = compiler.CompileToDocument();
        var expected = document.GetOutputBytes();
        var path = GetTempFilePath(".pdf");

        try
        {
            await File.WriteAllBytesAsync(path, new byte[expected.Length * 2]);
            document.WriteOutputToFile(path);
            var written = await File.ReadAllBytesAsync(path);

            await Assert.That(written.Length).IsEqualTo(expected.Length);
            await Assert.That(written.SequenceEqual(expected)).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MultiPageFilesHaveDistinctContent()
    {
        using var compiler = TypstCompiler.FromSource(TwoPageSource);
        var directory = CreateTempDirectory();

        try
        {
            compiler.Compile(Path.Combine(directory, "page.png"), "png");
            var first = await File.ReadAllBytesAsync(Path.Combine(directory, "page-1.png"));
            var second = await File.ReadAllBytesAsync(Path.Combine(directory, "page-2.png"));

            await Assert.That(first.Length).IsGreaterThan(0);
            await Assert.That(second.Length).IsGreaterThan(0);
            await Assert.That(first.SequenceEqual(second)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // PNG and SVG render one buffer per page, so the copying path has to surface all of them.
    [Test]
    public async Task CompileStillReturnsOneBufferPerPageForPng()
    {
        using var compiler = TypstCompiler.FromSource(TwoPageSource);

        var outcome = compiler.Compile("png");

        // Buffers is the member under test here, obsolete or not.
#pragma warning disable CS0618
        await Assert.That(outcome.Buffers.Count).IsEqualTo(2);
        await Assert.That(outcome.Buffers[0].Length).IsGreaterThan(0);
        await Assert.That(outcome.Buffers[0].SequenceEqual(outcome.Buffers[1])).IsFalse();
#pragma warning restore CS0618
    }

    [Test]
    public async Task PdfOutputIsASingleBufferRegardlessOfPageCount()
    {
        using var compiler = TypstCompiler.FromSource(TwoPageSource);
        using var document = compiler.CompileToDocument();

        await Assert.That(document.OutputCount).IsEqualTo(1);
    }

    // An unresolvable font family warns without failing the compilation, so the streaming overloads
    // have something to report through the only channel they still have.
    private const string WarningSource = """
                                         #set text(font: "No Such Font Family")
                                         Warned
                                         """;

    [Test]
    public async Task StreamingToAStreamReturnsCompilerWarnings()
    {
        using var compiler = TypstCompiler.FromSource(WarningSource);
        using var target = new MemoryStream();

        var warnings = compiler.CompilePdf(target);

        await Assert.That(warnings.Count).IsGreaterThan(0);
        await Assert.That(GetPlainText(target.ToArray())).Contains("Warned");
    }

    [Test]
    public async Task StreamingToAStreamAsynchronouslyReturnsCompilerWarnings()
    {
        using var compiler = TypstCompiler.FromSource(WarningSource);
        using var target = new MemoryStream();

        var warnings = await compiler.CompilePdfAsync(target);

        await Assert.That(warnings.Count).IsGreaterThan(0);
        await Assert.That(GetPlainText(target.ToArray())).Contains("Warned");
    }

    [Test]
    public async Task StreamingToAFileReturnsCompilerWarnings()
    {
        using var compiler = TypstCompiler.FromSource(WarningSource);
        var path = GetTempFilePath(".pdf");

        try
        {
            var warnings = compiler.CompilePdf(path);

            await Assert.That(warnings.Count).IsGreaterThan(0);
            await Assert.That(GetPlainText(await File.ReadAllBytesAsync(path))).Contains("Warned");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task StreamingToAFileAsynchronouslyReturnsCompilerWarnings()
    {
        using var compiler = TypstCompiler.FromSource(WarningSource);
        var path = GetTempFilePath(".pdf");

        try
        {
            var warnings = await compiler.CompilePdfAsync(path);

            await Assert.That(warnings.Count).IsGreaterThan(0);
            await Assert.That(GetPlainText(await File.ReadAllBytesAsync(path))).Contains("Warned");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task WarningsFromADocumentCannotBeMutatedByCallers()
    {
        using var compiler = TypstCompiler.FromSource(WarningSource);
        using var document = compiler.CompileToDocument();

        await Assert.That(document.Warnings.Count).IsGreaterThan(0);
        await Assert.That(document.Warnings is string[]).IsFalse();
    }

    private const string TwoPageSource = """
                                         First page
                                         #pagebreak()
                                         Second page
                                         """;

    private static string GetTempFilePath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"typstsharp-test-{Guid.NewGuid():N}{extension}");

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"typstsharp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetPlainText(byte[] pdf)
    {
        var sb = new StringBuilder();
        using (PdfDocument document = PdfDocument.Open(pdf))
        {
            foreach (Page page in document.GetPages())
            {
                string text = ContentOrderTextExtractor.GetText(page);
                sb.AppendLine(text);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// A throwaway package directory laid out the way Typst expects: one directory level per
/// namespace, package name and version.
/// </summary>
internal sealed class PackageDirectory : IDisposable
{
    public PackageDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"typstsharp-packages-{Guid.NewGuid():N}");

    public void AddPackage(string @namespace, string name, string version, string entrypointSource)
    {
        var directory = System.IO.Path.Combine(Path, @namespace, name, version);
        Directory.CreateDirectory(directory);

        File.WriteAllText(System.IO.Path.Combine(directory, "typst.toml"), $"""
            [package]
            name = "{name}"
            version = "{version}"
            entrypoint = "lib.typ"
            """);
        File.WriteAllText(System.IO.Path.Combine(directory, "lib.typ"), entrypointSource);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
