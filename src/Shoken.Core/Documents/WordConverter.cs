using System.IO.Packaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocSharp.Docx;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml;
using A = DocumentFormat.OpenXml.Drawing;
using V = DocumentFormat.OpenXml.Vml;
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
/// <param name="temporaryDirectory">Donde DocSharp extrae las imágenes mientras convierte; null = la carpeta temporal del sistema.</param>
public sealed partial class WordConverter(string imagesDirectory, string? temporaryDirectory = null)
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

        string extracted = Path.Combine(temporaryDirectory ?? Path.GetTempPath(), "shoken-word-" + Guid.NewGuid().ToString("N"));
        // Distinto en cada conversión: un texto del documento no puede pasar por marcador.
        string marker = "SHOKENAVISO" + Guid.NewGuid().ToString("N");
        try
        {
            List<string> notices;
            Dictionary<string, Queue<string>> altTexts;
            string markdown;
            try
            {
                using WordprocessingDocument document = WordprocessingDocument.Open(copy, isEditable: true);
                (notices, altTexts) = PrepareDrawings(document, marker);
                var converter = new DocxToMarkdownConverter { ExportHeaderFooter = false, ImagesOutputFolder = extracted };
                using var writer = new StringWriter();
                converter.Convert(document, writer);
                markdown = writer.ToString();
            }
            catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
            {
                // Paquete o XML dañado, contraseña, o algo que DocSharp no entiende: todo es «no se pudo abrir».
                throw new InvalidDataException(ConversionError, ex);
            }

            markdown = StoreImages(markdown, extracted, altTexts);
            markdown = Regex.Replace(markdown, marker + @"N(\d+)Z", m =>
                int.TryParse(m.Groups[1].Value, out int i) && i < notices.Count
                    ? $"![{EscapeAlt(notices[i])}]({UnavailableImageUrl})"
                    : m.Value);
            return FixNotes(markdown);
        }
        finally
        {
            try
            {
                if (Directory.Exists(extracted))
                {
                    Directory.Delete(extracted, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un antivirus que retiene una imagen extraída no puede tirar una conversión que salió bien.
            }
        }
    }

    /// <summary>
    /// DocSharp descarta sin avisar los gráficos de Word y las imágenes que no sabe convertir (EMF), y pone como texto
    /// alternativo el id de la relación («rId4»). Antes de convertir, cada gráfico o imagen que no se muestra pasa a un
    /// marcador de texto (alfanumérico, para que no se escape), y del resto se anota el texto alternativo real por id,
    /// en el orden del documento (el mismo id puede repetirse con otro texto). Se miran los dibujos (w:drawing) y las
    /// imágenes VML de documentos viejos y objetos pegados (w:pict, w:object).
    /// </summary>
    private static (List<string> Notices, Dictionary<string, Queue<string>> AltTexts) PrepareDrawings(WordprocessingDocument document, string marker)
    {
        var notices = new List<string>();
        var altTexts = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        MainDocumentPart? main = document.MainDocumentPart;
        if (main?.Document?.Body is not { } body)
        {
            return (notices, altTexts);
        }

        static bool IsPicture(OpenXmlElement e) => e is W.Drawing or W.Picture or W.EmbeddedObject;
        foreach (OpenXmlElement picture in body.Descendants().Where(IsPicture).Where(e => !e.Ancestors().Any(IsPicture)).ToList())
        {
            string alt;
            string? embed;
            bool notice;
            if (picture is W.Drawing)
            {
                Wp.DocProperties? properties = picture.Descendants<Wp.DocProperties>().FirstOrDefault();
                alt = properties?.Description?.Value is { Length: > 0 } description ? description : properties?.Title?.Value ?? "";
                embed = picture.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
                notice = picture.Descendants<A.GraphicData>().Any(g => g.Uri?.Value == ChartUri);
            }
            else
            {
                alt = picture.Descendants<V.Shape>().FirstOrDefault()?.Alternate?.Value ?? "";
                embed = picture.Descendants<V.ImageData>().FirstOrDefault()?.RelationshipId?.Value;
                notice = picture is W.EmbeddedObject; // un objeto pegado sin vista previa
            }

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
            else if (notice || embed is not null)
            {
                picture.InsertAfterSelf(new W.Text($"{marker}N{notices.Count}Z"));
                picture.Remove();
                notices.Add(alt);
            }
        }

        return (notices, altTexts);
    }

    /// <summary>
    /// Cada imagen extraída por DocSharp («![rId4](file:///…/image1.png)») se guarda con el SHA-256 de su contenido
    /// como nombre y se reescribe con una URL que no depende de la máquina ni de la ruta del documento. Se reconocen
    /// por la carpeta de extracción, no por el id (los generadores no usan «rId…») ni por el «)» que cierra el enlace
    /// (la ruta puede tener paréntesis, como «Juan (Admin)»).
    /// </summary>
    private string StoreImages(string markdown, string extracted, Dictionary<string, Queue<string>> altTexts)
    {
        string folder = extracted.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string escaped = Regex.Escape(new Uri(folder).AbsoluteUri);
        string plain = Regex.Escape("file:///" + folder.Replace('\\', '/'));
        var image = new Regex($@"!\[([^\]]*)\]\((?:{escaped}|{plain})([^)\s/]+)\)");
        return image.Replace(markdown, m =>
        {
            string file = Path.Combine(folder, Uri.UnescapeDataString(m.Groups[2].Value));
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
    }

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
    /// DocSharp escribe las notas al pie como «[1]» y «[1]: texto», y las del final como «[i]» y «[i]: texto», que
    /// Markdig toma por enlaces (el texto de la nota desaparece). Pasan a «[^1]» y «[^i]», como las notas de un .md: se
    /// muestran y no se leen. Solo se tocan las etiquetas que tienen su definición; los corchetes del texto común llegan
    /// escapados («\[1\]»).
    /// </summary>
    private static string FixNotes(string markdown)
    {
        var labels = NoteDefinition().Matches(markdown).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (labels.Count == 0)
        {
            return markdown;
        }

        markdown = NoteDefinition().Replace(markdown, "[^$1]: ");
        return NoteReference().Replace(markdown, m => labels.Contains(m.Groups[1].Value) ? $"[^{m.Groups[1].Value}]" : m.Value);
    }

    [GeneratedRegex(@"(?m)^\[(\d+|[ivxlcdm]+)\]:[ \t]*")]
    private static partial Regex NoteDefinition();

    // Una llamada puede ir pegada a «:» o a otra llamada; solo «(» la haría un enlace.
    [GeneratedRegex(@"(?<!\\)\[(\d+|[ivxlcdm]+)\](?!\()")]
    private static partial Regex NoteReference();
}
