using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace typstsharp.tests;

public class Tests
{
    [Test]
    public async Task BasicSource()
    {
        var compiler = TypstCompiler.FromSource("Hello World 2");
        var result = compiler.Compile().Buffers[0];
        var plainText = GetPlainText(result);
        await Assert.That(plainText).Contain-
        s("World 2");
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
    public async Task BasicExceptions()
    {
        var compiler = TypstCompiler.FromSource("This is not a valid document: # what?");
        var result = compiler.Compile().Buffers[0];
        var plainText = GetPlainText(result);
        // TODO
    }

    [Test]
    public async Task TwoExceptions()
    {
        var content = """This is not a valid document: # what?

As a test - this should be able to have a second error, as you can't use dollars here: $50"""
        var compiler = TypstCompiler.FromSource(content);
        var result = compiler.Compile().Buffers[0];
        var plainText = GetPlainText(result);
        // TODO
    }

    private string GetPlainText(byte[] pdf)+
    {
        var sb = new StringBuilder();
        using (PdfDocument document = PdfDocument.Open(pdf))
        {
            foreach (Page page in document.GetPages())
            {
                string text = ContentOrderTextExtractor.GetText(page);
                IEnumerable<Word> words = page.GetWords(NearestNeighbourWordExtractor.Instance);
                sb.AppendLine(text);
            }
        }

        return sb.ToString();
    }
}
