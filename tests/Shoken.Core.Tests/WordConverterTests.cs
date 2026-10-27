using DocumentFormat.OpenXml.Packaging;
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
        // La app lo muestra después de «No se pudo abrir "x.docx":», así que no lo repite.
        Assert.Equal("El documento de Word puede estar dañado o protegido con contraseña.", ex.Message);
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

    // ------------------------------------------------------------------ documentos que no hizo Word

    /// <summary>PNG de 1×1.</summary>
    private static readonly byte[] Png = System.Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"";

    /// <summary>Un .docx armado a mano (como lo haría un generador de reportes), con sus imágenes en esos ids.</summary>
    private static MemoryStream BuildDocx(string bodyXml, params (string Id, string ContentType, byte[] Bytes)[] images)
    {
        var stream = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            foreach (var (id, contentType, bytes) in images)
            {
                main.AddImagePart(contentType, id).FeedData(new MemoryStream(bytes));
            }

            using var writer = new StreamWriter(main.GetStream(FileMode.Create));
            writer.Write($"<w:document {Namespaces}><w:body>{bodyXml}</w:body></w:document>");
        }

        stream.Position = 0;
        return stream;
    }

    private static string Paragraph(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static string InlineImage(string relationshipId, string alt) =>
        "<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"914400\"/>" +
        $"<wp:docPr id=\"1\" name=\"Imagen\" descr=\"{alt}\"/>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
        "<pic:nvPicPr><pic:cNvPr id=\"0\" name=\"img.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
        $"<pic:blipFill><a:blip r:embed=\"{relationshipId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
        "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"rect\"/></pic:spPr>" +
        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";

    private static string VmlImage(string relationshipId, string alt) =>
        $"<w:p><w:r><w:pict><v:shape id=\"s1\" style=\"width:20pt;height:20pt\" alt=\"{alt}\"><v:imagedata r:id=\"{relationshipId}\"/></v:shape></w:pict></w:r></w:p>";

    private string ConvertStream(Stream docx, string? temporaryDirectory = null) =>
        new WordConverter(Images, temporaryDirectory).Convert(docx);

    [Fact]
    public void Word_ImageWithGeneratorRelationshipId_IsStoredByContentHash()
    {
        string sha = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Png));

        string md = ConvertStream(BuildDocx(Paragraph("Antes.") + InlineImage("R3f2a9c", "Un punto") + Paragraph("Después."),
            ("R3f2a9c", "image/png", Png)));

        Assert.Contains($"![Un punto](https://img.lector/{sha}.png)", md);
        Assert.DoesNotContain("file:", md);
    }

    [Fact]
    public void Word_TemporaryFolderWithParenthesesAndSpaces_StillStoresImages()
    {
        string temp = Path.Combine(_dir, "Juan (Admin) temp");

        string md = ConvertStream(BuildDocx(InlineImage("rId9", "Logo"), ("rId9", "image/png", Png)), temp);

        Assert.Contains("](https://img.lector/", md);
        Assert.DoesNotContain("file:", md);
    }

    [Fact]
    public void Word_MarkerLikeTextInTheDocument_StaysText()
    {
        string md = ConvertStream(BuildDocx(Paragraph("El marcador SHOKENAVISO0Z se describe aquí.")));

        Assert.Contains("SHOKENAVISO0Z", md);
        Assert.DoesNotContain("imagen-no-disponible", md);
    }

    [Fact]
    public void Word_MalformedXml_ThrowsInvalidData()
    {
        var stream = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            using var writer = new StreamWriter(doc.AddMainDocumentPart().GetStream(FileMode.Create));
            writer.Write($"<w:document {Namespaces}><w:body><w:p>");
        }

        stream.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => ConvertStream(stream));
        Assert.Equal(WordConverter.ConversionError, ex.Message);
    }

    [Fact]
    public void Word_VmlEmf_BecomesTheUnavailableNotice()
    {
        string md = ConvertStream(BuildDocx(Paragraph("Antes.") + VmlImage("rId5", "Objeto de Excel") + Paragraph("Después."),
            ("rId5", "image/x-emf", [1, 2, 3, 4])));

        Assert.Contains("![Objeto de Excel](https://app.lector/imagen-no-disponible.svg)", md);
    }

    [Fact]
    public void Word_VmlPng_KeepsItsAltText()
    {
        string md = ConvertStream(BuildDocx(VmlImage("rId6", "Logo viejo"), ("rId6", "image/png", Png)));

        Assert.Contains("![Logo viejo](https://img.lector/", md);
    }

    [Fact]
    public void Word_ConcurrentConversions_StoreEachImageWhole()
    {
        byte[] docx = File.ReadAllBytes(Path.Combine(Fixtures, "imagenes.docx"));

        // Dos guardados seguidos, o «Copiar el texto Markdown» durante una recarga: varias conversiones a la vez.
        Parallel.For(0, 16, _ => new WordConverter(Images).Convert(new MemoryStream(docx)));

        string[] files = Directory.GetFiles(Images);
        Assert.Equal(2, files.Length);
        foreach (string file in files)
        {
            string sha = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            Assert.Equal(sha, Path.GetFileNameWithoutExtension(file));
        }
    }

    /// <summary>Un .docx con una imagen en el cuerpo y otra (y un EMF) dentro de una nota al pie, con el mismo id.</summary>
    private static MemoryStream BuildDocxWithImageInFootnote(byte[] bodyImage, byte[] footnoteImage)
    {
        var stream = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.AddImagePart("image/png", "rId1").FeedData(new MemoryStream(bodyImage));
            var footnotes = main.AddNewPart<FootnotesPart>("rIdNotas");
            footnotes.AddImagePart("image/png", "rId1").FeedData(new MemoryStream(footnoteImage));
            footnotes.AddImagePart("image/x-emf", "rId2").FeedData(new MemoryStream([1, 2, 3, 4]));
            using (var writer = new StreamWriter(footnotes.GetStream(FileMode.Create)))
            {
                writer.Write($"<w:footnotes {Namespaces}>" +
                    "<w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>" +
                    "<w:footnote w:type=\"continuationSeparator\" w:id=\"0\"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>" +
                    "<w:footnote w:id=\"1\"><w:p><w:r><w:t xml:space=\"preserve\">Nota con imagen. </w:t></w:r></w:p>" +
                    InlineImage("rId1", "Imagen de la nota") + VmlImage("rId2", "EMF de la nota") + "</w:footnote></w:footnotes>");
            }

            using var body = new StreamWriter(main.GetStream(FileMode.Create));
            body.Write($"<w:document {Namespaces}><w:body>" +
                "<w:p><w:r><w:t xml:space=\"preserve\">Texto con nota</w:t></w:r><w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>" +
                InlineImage("rId1", "Imagen del cuerpo") + "</w:body></w:document>");
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Word_ImagesInFootnotes_KeepTheirOwnAltText_AndEmfGivesTheNotice()
    {
        byte[] other = (byte[])Png.Clone();
        other[^5] ^= 0xFF; // otra imagen (otro SHA-256), aunque no sea un PNG válido
        string bodySha = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Png));
        string noteSha = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(other));

        string md = ConvertStream(BuildDocxWithImageInFootnote(Png, other));

        Assert.Contains($"![Imagen del cuerpo](https://img.lector/{bodySha}.png)", md);
        Assert.Contains($"![Imagen de la nota](https://img.lector/{noteSha}.png)", md);
        Assert.Contains("![EMF de la nota](https://app.lector/imagen-no-disponible.svg)", md);
    }

    // ------------------------------------------------------------------ notas al pie y al final

    [Fact]
    public void Word_FootnoteBeforeColon_IsConverted()
    {
        string md = Convert("notas.docx");

        Assert.Contains("el autor[^", md);
        Assert.DoesNotMatch(@"(?<!\\)\[\d+\](?!:)", md.Split('\n').First(l => l.StartsWith("Como dice", StringComparison.Ordinal)));
    }

    [Fact]
    public void Word_TwoFootnotesTogether_AreBothConverted()
    {
        string line = Convert("notas.docx").Split('\n').First(l => l.StartsWith("Dos notas", StringComparison.Ordinal));

        Assert.Matches(@"seguidas\[\^\w+\]\[\^\w+\]", line);
    }

    [Fact]
    public void Word_Endnotes_AreShownButNotRead_LikeFootnotes()
    {
        string md = Convert("notas.docx");

        var doc = new MarkdownSpeechParser().Parse(md);
        Assert.Contains("Texto de la nota final.", doc.HtmlBody);
        Assert.DoesNotContain(doc.Segments, s => s.PlainText.Contains("nota final.", StringComparison.Ordinal) && s.PlainText.Contains("Texto", StringComparison.Ordinal));
        Assert.DoesNotContain(doc.Segments, s => s.PlainText.Contains('[', StringComparison.Ordinal));
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
