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
    public async Task CompileToStreamProducesReadablePdf()
    {
        using var compiler = TypstCompiler.FromSource("= Compiled into a stream");
        using var target = new MemoryStream();

        compiler.CompileToStream(target);

        await Assert.That(GetPlainText(target.ToArray())).Contains("Compiled into a stream");
    }

    [Test]
    public async Task CompileToStreamAsyncProducesReadablePdf()
    {
        using var compiler = TypstCompiler.FromSource("= Compiled into a stream asynchronously");
        using var target = new MemoryStream();

        await compiler.CompileToStreamAsync(target);

        await Assert.That(GetPlainText(target.ToArray())).Contains("asynchronously");
    }

    [Test]
    public async Task CompileToFileProducesReadablePdf()
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
    public async Task CompileToFileAsyncProducesReadablePdf()
    {
        using var compiler = TypstCompiler.FromSource("= Compiled to a file asynchronously");
        var path = GetTempFilePath(".pdf");

        try
        {
            await compiler.CompileToFileAsync(path);

            await Assert.That(GetPlainText(await File.ReadAllBytesAsync(path))).Contains("asynchronously");
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
    public async Task MultiBufferFormatCannotBeWrittenToASingleStream()
    {
        using var compiler = TypstCompiler.FromSource(TwoPageSource);
        using var target = new MemoryStream();

        await Assert.That(() => compiler.CompileToStream(target, "png")).Throws<ArgumentException>();
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
        // An unresolvable font family warns without failing the compilation.
        using var compiler = TypstCompiler.FromSource("""
                                                      #set text(font: "No Such Font Family")
                                                      Warned
                                                      """);
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
            compiler.CompileToFile(Path.Combine(directory, "page.png"), "png");
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

    // Compile() now routes through the document, so its multi-buffer behaviour has to be unchanged.
    [Test]
    public async Task CompileStillReturnsOneBufferPerPageForPng()
    {
        using var compiler = TypstCompiler.FromSource(TwoPageSource);

        var outcome = compiler.Compile("png");

        await Assert.That(outcome.Buffers.Count).IsEqualTo(2);
        await Assert.That(outcome.Buffers[0].Length).IsGreaterThan(0);
        await Assert.That(outcome.Buffers[0].SequenceEqual(outcome.Buffers[1])).IsFalse();
    }

    [Test]
    public async Task PdfOutputIsASingleBufferRegardlessOfPageCount()
    {
        using var compiler = TypstCompiler.FromSource(TwoPageSource);
        using var document = compiler.CompileToDocument();

        await Assert.That(document.OutputCount).IsEqualTo(1);
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