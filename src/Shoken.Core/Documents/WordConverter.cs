using System.IO.Packaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocSharp.Docx;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Shoken.Core.Documents;

/// <summary>
/// Convierte un documento de Word (.docx) en el Markdown que lee la app, con DocSharp. El resultado tiene que ser
/// estable: el mismo archivo da siempre el mismo Markdown, desde cualquier ruta o máquina, porque de él salen el
/// <c>BlockKey</c> de lo escuchado y la clave de la caché de audio.
/// </summary>
/// <param name="imagesDirectory">
/// Donde quedan las imágenes del documento, con el SHA-256 de su contenido como nombre; la app la sirve como
/// <see cref="WordImagesHost"/>.
/// </param>
public sealed partial class WordConverter(string imagesDirectory)
{
    public const string ConversionError = "No se pudo abrir el documento de Word: puede estar dañado o protegido con contraseña.";

    /// <summary>Host virtual de <c>imagesDirectory</c>. Queda dentro del Markdown (y del BlockKey): no lo cambies.</summary>
    public const string WordImagesHost = "img.lector";

    /// <summary>Lo que se muestra en lugar de una imagen que el navegador no dibuja (EMF) o de un gráfico de Word.</summary>
    public const string UnavailableImageUrl = "https://app.lector/imagen-no-disponible.svg";

    /// <summary>Tipos de imagen que el WebView2 muestra; los demás (EMF, WMF, TIFF...) van como aviso.</summary>
    private static readonly HashSet<string> ShownImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/bmp", "image/webp", "image/svg+xml",
    };

    private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";

    public static bool IsWordFile(string path) => string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase);

    /// <exception cref="InvalidDataException">El archivo no es un .docx que se pueda abrir.</exception>
    public string Convert(Stream docx)
    {
        var copy = new MemoryStream();
        docx.CopyTo(copy);
        copy.Position = 0;

        string extracted = Path.Combine(Path.GetTempPath(), "shoken-word-" + Guid.NewGuid().ToString("N"));
        try
        {
            string markdown;
            List<string> notices;
            Dictionary<string, Queue<string>> altTexts;
            try
            {
                using WordprocessingDocument document = WordprocessingDocument.Open(copy, isEditable: true);
                (notices, altTexts) = PrepareDrawings(document);
                var converter = new DocxToMarkdownConverter { ExportHeaderFooter = false, ImagesOutputFolder = extracted };
                using var writer = new StringWriter();
                converter.Convert(document, writer);
                markdown = writer.ToString();
            }
            catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException)
            {
                throw new InvalidDataException(ConversionError, ex);
            }

            markdown = StoreImages(markdown, altTexts);
            markdown = NoticeMarker().Replace(markdown, m => $"![{EscapeAlt(notices[int.Parse(m.Groups[1].Value)])}]({UnavailableImageUrl})");
            return FixFootnotes(markdown);
        }
        finally
        {
            if (Directory.Exists(extracted))
            {
                Directory.Delete(extracted, recursive: true);
            }
        }
    }

    /// <summary>
    /// DocSharp descarta sin avisar los gráficos de Word y las imágenes que no sabe convertir (EMF), y pone como texto
    /// alternativo el id de la relación («rId4»). Antes de convertir, cada gráfico o imagen que no se muestra pasa a un
    /// marcador de texto (alfanumérico, para que no se escape), y del resto se anota el texto alternativo real por id,
    /// en el orden del documento (el mismo id puede repetirse con otro texto).
    /// </summary>
    private static (List<string> Notices, Dictionary<string, Queue<string>> AltTexts) PrepareDrawings(WordprocessingDocument document)
    {
        var notices = new List<string>();
        var altTexts = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        MainDocumentPart? main = document.MainDocumentPart;
        if (main?.Document?.Body is not { } body)
        {
            return (notices, altTexts);
        }

        foreach (W.Drawing drawing in body.Descendants<W.Drawing>().ToList())
        {
            Wp.DocProperties? properties = drawing.Descendants<Wp.DocProperties>().FirstOrDefault();
            string alt = properties?.Description?.Value is { Length: > 0 } description ? description : properties?.Title?.Value ?? "";
            string? embed = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
            bool chart = drawing.Descendants<A.GraphicData>().Any(g => g.Uri?.Value == ChartUri);
            bool shown = embed is not null && main.TryGetPartById(embed, out OpenXmlPart? part) && part is ImagePart image &&
                         ShownImageTypes.Contains(image.ContentType);

            if (shown)
            {
                if (!altTexts.TryGetValue(embed!, out Queue<string>? queue))
                {
                    altTexts[embed!] = queue = new Queue<string>();
                }

                queue.Enqueue(alt);
            }
            else if (chart || embed is not null)
            {
                drawing.InsertAfterSelf(new W.Text($"SHOKENAVISO{notices.Count}Z"));
                drawing.Remove();
                notices.Add(alt);
            }
        }

        return (notices, altTexts);
    }

    /// <summary>
    /// Cada imagen extraída por DocSharp («![rId4](file:///…/image1.png)») se guarda con el SHA-256 de su contenido
    /// como nombre y se reescribe con una URL que no depende de la máquina ni de la ruta del documento.
    /// </summary>
    private string StoreImages(string markdown, Dictionary<string, Queue<string>> altTexts) =>
        ExtractedImage().Replace(markdown, m =>
        {
            string file = new Uri(m.Groups[2].Value).LocalPath;
            byte[] bytes = File.ReadAllBytes(file);
            string name = System.Convert.ToHexStringLower(SHA256.HashData(bytes)) + Path.GetExtension(file).ToLowerInvariant();
            string target = Path.Combine(imagesDirectory, name);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(imagesDirectory);
                File.WriteAllBytes(target, bytes);
            }

            string alt = altTexts.TryGetValue(m.Groups[1].Value, out Queue<string>? queue) && queue.Count > 0 ? queue.Dequeue() : "";
            return $"![{EscapeAlt(alt)}](https://{WordImagesHost}/{name})";
        });

    /// <summary>El texto alternativo en una sola línea y sin caracteres que Markdown tome por sintaxis.</summary>
    private static string EscapeAlt(string alt)
    {
        var text = new StringBuilder(alt.Length);
        foreach (char c in alt.ReplaceLineEndings(" "))
        {
            if (c is '\\' or '[' or ']' or '*' or '_')
            {
                text.Append('\\');
            }

            text.Append(c);
        }

        return text.ToString().Trim();
    }

    /// <summary>
    /// DocSharp escribe las notas como «[1]» y «[1]: texto», que Markdig toma por un enlace (el texto de la nota
    /// desaparece). Pasan a «[^1]». Los corchetes del texto común llegan escapados («\[1\]») y no se tocan.
    /// </summary>
    private static string FixFootnotes(string markdown)
    {
        markdown = FootnoteDefinition().Replace(markdown, "[^$1]: ");
        return FootnoteReference().Replace(markdown, "[^$1]");
    }

    [GeneratedRegex(@"!\[(rId\w+)\]\((file:[^)]+)\)")]
    private static partial Regex ExtractedImage();

    [GeneratedRegex(@"SHOKENAVISO(\d+)Z")]
    private static partial Regex NoticeMarker();

    [GeneratedRegex(@"(?m)^\[(\d+)\]:[ \t]*")]
    private static partial Regex FootnoteDefinition();

    [GeneratedRegex(@"(?<!\\)\[(\d+)\](?![:(\[])")]
    private static partial Regex FootnoteReference();
}
