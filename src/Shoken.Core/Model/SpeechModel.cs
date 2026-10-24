namespace Shoken.Core.Model;

/// <summary>Estilo inline de un fragmento de texto (combinable).</summary>
[Flags]
public enum RunStyle
{
    None = 0,
    Strong = 1,
    Emphasis = 2,
    Code = 4,
    Link = 8,
}

/// <summary>Un tramo de texto con un estilo uniforme dentro de un segmento.</summary>
public sealed record SpeechRun(string Text, RunStyle Style = RunStyle.None);

public enum SegmentKind
{
    Heading,
    Paragraph,
    ListItem,
    Quote,
    AlertLabel,
    TableHeader,
    TableRow,
    CodeBlock,
    Math,
}

/// <summary>
/// Unidad mínima de lectura: un encabezado, un párrafo, un elemento de lista, una fila de tabla...
/// Cada segmento se asocia a un elemento HTML (<see cref="AnchorId"/>) para resaltarlo mientras se lee.
/// </summary>
public sealed class SpeechSegment
{
    private string? _plainText;

    /// <summary>Índice del segmento dentro del documento (0..n-1).</summary>
    public required int Id { get; init; }

    /// <summary>Valor del atributo <c>data-seg</c> del elemento HTML que representa este segmento.</summary>
    public required string AnchorId { get; init; }

    public required SegmentKind Kind { get; init; }

    /// <summary>Nivel 1..6 para encabezados; 0 en otro caso.</summary>
    public int HeadingLevel { get; init; }

    public required IReadOnlyList<SpeechRun> Runs { get; init; }

    /// <summary>Pausa deseada antes del segmento (se suma a la pausa posterior del anterior).</summary>
    public int PauseBeforeMs { get; init; }

    /// <summary>Pausa deseada después del segmento.</summary>
    public int PauseAfterMs { get; init; }

    /// <summary>Línea (1-based) del Markdown de origen.</summary>
    public int Line { get; init; }

    /// <summary>Última línea (1-based) del bloque en el Markdown: un párrafo puede ocupar varias.</summary>
    public int EndLine { get; init; }

    public bool IsHeading => Kind == SegmentKind.Heading;

    public string PlainText => _plainText ??= string.Concat(Runs.Select(r => r.Text));

    public override string ToString() =>
        $"#{Id} {Kind}{(IsHeading ? HeadingLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")} [{AnchorId}]: {PlainText}";
}

/// <summary>Resultado de analizar un Markdown: segmentos para la voz y HTML para mostrarlo.</summary>
public sealed class SpeechDocument
{
    public required string Title { get; init; }

    /// <summary>Segmentos en orden de lectura; <c>Segments[i].Id == i</c>.</summary>
    public required IReadOnlyList<SpeechSegment> Segments { get; init; }

    /// <summary>Cuerpo HTML con atributos <c>data-seg</c> en cada bloque legible.</summary>
    public required string HtmlBody { get; init; }

    public string? SourcePath { get; init; }

    public SpeechSegment? FirstForAnchor(string anchorId) =>
        Segments.FirstOrDefault(s => string.Equals(s.AnchorId, anchorId, StringComparison.Ordinal));

    /// <summary>Encabezado de la sección a la que pertenece el segmento (o null si está antes del primer encabezado).</summary>
    public SpeechSegment? SectionOf(int segmentId)
    {
        for (int i = Math.Min(segmentId, Segments.Count - 1); i >= 0; i--)
        {
            if (Segments[i].IsHeading)
            {
                return Segments[i];
            }
        }

        return null;
    }

    public int CharacterCount => Segments.Sum(s => s.PlainText.Length);
}
