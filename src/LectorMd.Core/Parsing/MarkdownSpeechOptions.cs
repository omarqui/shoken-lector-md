namespace LectorMd.Core.Parsing;

public enum CodeBlockMode
{
    /// <summary>Dice "Bloque de código en C#." y sigue.</summary>
    Announce,

    /// <summary>Lo salta en silencio (con una pausa breve).</summary>
    Skip,

    /// <summary>Lee el contenido línea por línea.</summary>
    Read,
}

public enum TableMode
{
    /// <summary>Anuncia las columnas y lee cada fila como "Columna: valor."</summary>
    ReadRows,

    /// <summary>Solo dice "Tabla de N columnas y M filas."</summary>
    Announce,

    /// <summary>La salta.</summary>
    Skip,
}

public sealed class MarkdownSpeechOptions
{
    public CodeBlockMode CodeBlocks { get; init; } = CodeBlockMode.Announce;

    public TableMode Tables { get; init; } = TableMode.ReadRows;

    /// <summary>Si es true, las imágenes se leen como "Imagen: texto alternativo".</summary>
    public bool ReadImageAltText { get; init; }

    /// <summary>Los párrafos más largos que esto se dividen por oraciones (mismo elemento resaltado).</summary>
    public int LongSegmentChars { get; init; } = 1200;

    public PauseProfile Pauses { get; init; } = new();
}

/// <summary>Pausas (en milisegundos) que se derivan de la estructura del Markdown.</summary>
public sealed class PauseProfile
{
    /// <summary>Pausa antes de un encabezado, por nivel (h1..h6).</summary>
    public int[] HeadingBefore { get; init; } = [1100, 950, 750, 600, 550, 550];

    /// <summary>Pausa después de un encabezado, por nivel (h1..h6).</summary>
    public int[] HeadingAfter { get; init; } = [650, 550, 450, 350, 350, 350];

    public int Paragraph { get; init; } = 320;

    public int ListItem { get; init; } = 220;

    /// <summary>Pausa extra al entrar y salir de una cita o alerta.</summary>
    public int QuoteEdge { get; init; } = 300;

    public int TableRow { get; init; } = 300;

    public int CodeBlock { get; init; } = 400;

    /// <summary>Pausa extra por una línea horizontal (<c>---</c>).</summary>
    public int ThematicBreak { get; init; } = 700;

    /// <summary>Pausa extra al terminar una lista.</summary>
    public int ListEnd { get; init; } = 120;
}
