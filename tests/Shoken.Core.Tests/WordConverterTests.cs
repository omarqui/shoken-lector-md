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

    // ------------------------------------------------------------------ imágenes

    /// <summary>El SHA-256 (hex en minúsculas) de cada imagen de ese tipo que trae el .docx, en el orden del paquete.</summary>
    private static string[] ImageHashes(string fixture, string contentType)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(Path.Combine(Fixtures, fixture), false);
        return doc.MainDocumentPart!.ImageParts.Where(p => p.ContentType == contentType)
            .Select(p =>
            {
                using Stream s = p.GetStream();
                return System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s));
            })
            .Distinct()
            .ToArray();
    }

    [Fact]
    public void Word_Image_IsStoredByContentHash_WithItsAltText()
    {
        string sha = Assert.Single(ImageHashes("imagenes.docx", "image/png"));

        string md = Convert("imagenes.docx");

        Assert.Contains($"![Logo de Shoken](https://img.lector/{sha}.png)", md);
        Assert.True(File.Exists(Path.Combine(Images, sha + ".png")));
    }

    [Fact]
    public void Word_ImageWithoutAltText_HasEmptyAlt()
    {
        string sha = Assert.Single(ImageHashes("imagenes.docx", "image/png"));

        string md = Convert("imagenes.docx");

        Assert.Contains($"![](https://img.lector/{sha}.png)", md);
        Assert.DoesNotContain("rId", md);
    }

    [Fact]
    public void Word_SameImageTwice_IsStoredOnce()
    {
        Convert("imagenes.docx");

        Assert.Equal(2, Directory.GetFiles(Images).Length);
    }

    [Fact]
    public void Word_AltTextWithMarkdownCharacters_IsEscaped()
    {
        string md = Convert("imagenes.docx");

        var doc = new MarkdownSpeechParser(new MarkdownSpeechOptions { ReadImageAltText = true }).Parse(md);
        Assert.Contains(doc.Segments, s => s.PlainText.Contains("Imagen: Paso [2] *importante* con salto", StringComparison.Ordinal));
        Assert.Equal(3, doc.HtmlBody.Split("<img").Length - 1);
    }

    [Fact]
    public void Word_EmfAndChart_BecomeTheUnavailableNotice()
    {
        string md = Convert("especiales.docx");

        string notice = "(https://app.lector/imagen-no-disponible.svg)";
        int emf = md.IndexOf("![Diagrama EMF]" + notice, StringComparison.Ordinal);
        int chart = md.IndexOf("![Gráfico de ventas]" + notice, StringComparison.Ordinal);
        int bare = md.IndexOf("![]" + notice, StringComparison.Ordinal);
        Assert.Equal(3, md.Split(notice).Length - 1);
        Assert.True(md.IndexOf("Antes del EMF", StringComparison.Ordinal) < emf, md);
        Assert.True(emf < md.IndexOf("Entre el EMF y el gráfico", StringComparison.Ordinal), md);
        Assert.True(md.IndexOf("Entre el EMF y el gráfico", StringComparison.Ordinal) < chart, md);
        Assert.True(chart < md.IndexOf("Entre el gráfico y el EMF sin texto", StringComparison.Ordinal), md);
        Assert.True(md.IndexOf("Entre el gráfico y el EMF sin texto", StringComparison.Ordinal) < bare, md);
        Assert.True(bare < md.IndexOf("Después de todo", StringComparison.Ordinal), md);
    }

    [Fact]
    public void Word_NoFileUrls()
    {
        Assert.DoesNotContain("file:", Convert("imagenes.docx"));
        Assert.DoesNotContain("file:", Convert("especiales.docx"));
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
