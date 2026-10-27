using System.IO.Packaging;
using System.Text.RegularExpressions;
using DocSharp.Docx;
using DocumentFormat.OpenXml.Packaging;

namespace Shoken.Core.Documents;

/// <summary>
/// Convierte un documento de Word (.docx) en el Markdown que lee la app, con DocSharp. El resultado tiene que ser
/// estable: el mismo archivo da siempre el mismo Markdown, desde cualquier ruta o máquina, porque de él salen el
/// <c>BlockKey</c> de lo escuchado y la clave de la caché de audio.
/// </summary>
public sealed partial class WordConverter(string imagesDirectory)
{
    public const string ConversionError = "No se pudo abrir el documento de Word: puede estar dañado o protegido con contraseña.";

    /// <summary>Carpeta donde quedan las imágenes del documento (se usa desde la Tarea de imágenes).</summary>
    public string ImagesDirectory { get; } = imagesDirectory;

    public static bool IsWordFile(string path) => string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase);

    /// <exception cref="InvalidDataException">El archivo no es un .docx que se pueda abrir.</exception>
    public string Convert(Stream docx)
    {
        var copy = new MemoryStream();
        docx.CopyTo(copy);
        copy.Position = 0;

        string markdown;
        try
        {
            using WordprocessingDocument document = WordprocessingDocument.Open(copy, isEditable: true);
            var converter = new DocxToMarkdownConverter { ExportHeaderFooter = false };
            using var writer = new StringWriter();
            converter.Convert(document, writer);
            markdown = writer.ToString();
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException)
        {
            throw new InvalidDataException(ConversionError, ex);
        }

        return FixFootnotes(markdown);
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

    [GeneratedRegex(@"(?m)^\[(\d+)\]:[ \t]*")]
    private static partial Regex FootnoteDefinition();

    [GeneratedRegex(@"(?<!\\)\[(\d+)\](?![:(\[])")]
    private static partial Regex FootnoteReference();
}
