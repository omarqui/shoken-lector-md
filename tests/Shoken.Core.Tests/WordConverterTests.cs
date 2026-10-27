using Shoken.Core.Documents;
using Shoken.Core.Parsing;

namespace Shoken.Core.Tests;

public sealed class WordConverterTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Word");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shoken-word-" + Guid.NewGuid().ToString("N"));

    public WordConverterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Images => Path.Combine(_dir, "imagenes");

    private string Convert(string fixture) => ConvertFile(Path.Combine(Fixtures, fixture));

    private string ConvertFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new WordConverter(Images).Convert(stream);
    }

    [Fact]
    public void Word_Structure_MatchesExpected()
    {
        string expected = File.ReadAllText(Path.Combine(Fixtures, "estructura.expected.md"));

        Assert.Equal(expected, Convert("estructura.docx"));
    }

    [Fact]
    public void Word_HeaderFooterAndPageNumber_AreNotRead()
    {
        string md = Convert("estructura.docx");

        Assert.DoesNotContain("Shoken - documento de prueba", md);
        Assert.DoesNotContain("Shoken \\- documento de prueba", md);
        string last = md.TrimEnd().Split('\n')[^1].Trim();
        Assert.False(int.TryParse(last, out _), $"La última línea es un número de página: «{last}»");
    }

    [Fact]
    public void Word_Footnotes_UseMarkdigSyntax()
    {
        string md = Convert("estructura.docx");

        Assert.Contains("[^1]", md);
        Assert.Contains("\n[^1]: Esta es la nota al pie.", md);
        Assert.DoesNotContain("\n[1]:", md);
        // Como en un .md: la nota se muestra (al final de la página) pero no se lee, y su llamada no se lee como «[1]».
        var doc = new MarkdownSpeechParser().Parse(md);
        Assert.Contains("Esta es la nota al pie", doc.HtmlBody);
        Assert.DoesNotContain(doc.Segments, s => s.PlainText.Contains("[1]", StringComparison.Ordinal));
        Assert.Contains(doc.Segments, s => s.PlainText.Contains("una nota al pie. Sigue el párrafo.", StringComparison.Ordinal));
    }

    [Fact]
    public void Word_LiteralBracketNumber_StaysText()
    {
        string md = Convert("especiales-texto.docx");

        Assert.DoesNotContain("[^", md);
        var doc = new MarkdownSpeechParser().Parse(md);
        Assert.Contains(doc.Segments, s => s.PlainText.Contains("Ver el punto [1] del contrato.", StringComparison.Ordinal));
    }

    [Fact]
    public void Word_TrackedDeletion_IsNotRead()
    {
        Assert.DoesNotContain("BORRADO", Convert("especiales-texto.docx"));
    }

    [Fact]
    public void Word_Comments_AreNotRead()
    {
        Assert.DoesNotContain("COMENTARIO", Convert("especiales-texto.docx"));
    }

    [Fact]
    public void Word_SameFileFromTwoPaths_GivesSameMarkdown()
    {
        string a = Path.Combine(_dir, "a", "informe.docx");
        string b = Path.Combine(_dir, "otra carpeta", "copia.docx");
        foreach (string copy in new[] { a, b })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(Path.Combine(Fixtures, "estructura.docx"), copy);
        }

        Assert.Equal(ConvertFile(a), ConvertFile(b));
    }

    [Fact]
    public void Word_CorruptFile_ThrowsInvalidData()
    {
        var bytes = new byte[2048];
        new Random(7).NextBytes(bytes);

        var ex = Assert.Throws<InvalidDataException>(() => new WordConverter(Images).Convert(new MemoryStream(bytes)));
        Assert.Equal("No se pudo abrir el documento de Word: puede estar dañado o protegido con contraseña.", ex.Message);
    }

    [Fact]
    public void Word_IsWordFile_OnlyDocx()
    {
        Assert.True(WordConverter.IsWordFile(@"C:\a.docx"));
        Assert.True(WordConverter.IsWordFile(@"C:\A.DOCX"));
        Assert.False(WordConverter.IsWordFile(@"C:\a.doc"));
        Assert.False(WordConverter.IsWordFile(@"C:\a.md"));
        Assert.False(WordConverter.IsWordFile(@"C:\a.docxx"));
    }
}
