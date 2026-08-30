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
