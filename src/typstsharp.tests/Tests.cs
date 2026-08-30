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
        Console.WriteLine("This is a basic test");

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