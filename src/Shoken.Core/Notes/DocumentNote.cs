using System.Text.Json.Serialization;

namespace Shoken.Core.Notes;

/// <summary>Qué se le pide a la IA sobre el texto anotado.</summary>
public enum NoteKind
{
    /// <summary>"No entiendo esto": que lo explique y, si hace falta, lo reescriba más claro.</summary>
    Clarify,

    /// <summary>"Esto se puede decir mejor": que proponga una versión mejorada.</summary>
    Improve,

    /// <summary>Lo que diga la nota.</summary>
    Other,
}

/// <summary>
/// Un apunte sobre un texto del documento, como un comentario. Se ancla por su cita: al abrir o recargar se
/// vuelve a buscar, porque las anclas del HTML cambian si se edita el Markdown.
/// </summary>
public sealed class DocumentNote
{
    public required string Id { get; init; }

    /// <summary>El texto señalado tal como se ve (la selección, o el párrafo entero).</summary>
    public required string Quote { get; set; }

    /// <summary>Primera y última línea del Markdown donde se encontró la cita la última vez.</summary>
    public int Line { get; set; }

    public int EndLine { get; set; }

    /// <summary>Títulos que la contienen ("Guía › Instalación"), o null antes del primer título.</summary>
    public string? Section { get; set; }

    public NoteKind Kind { get; set; } = NoteKind.Clarify;

    public string Text { get; set; } = "";

    public bool Resolved { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>La cita ya no está en el documento. Se recalcula cada vez que se abre: no se guarda.</summary>
    [JsonIgnore]
    public bool Orphaned { get; set; }
}

/// <summary>Dónde está un apunte en el Markdown.</summary>
public sealed record NoteLocation(int Line, int EndLine, string? Section);
