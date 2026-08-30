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
