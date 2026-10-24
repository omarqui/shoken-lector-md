using Shoken.Core.Model;

namespace Shoken.Core.Notes;

/// <summary>Traduce lo que señala un apunte en la página (anclas data-seg) a líneas del Markdown y sección.</summary>
public static class NoteReferences
{
    /// <param name="startAnchor">data-seg del bloque donde empieza la cita.</param>
    /// <param name="endAnchor">data-seg del bloque donde termina (el mismo si no cruza bloques).</param>
    public static NoteLocation Describe(SpeechDocument document, IReadOnlyList<OutlineEntry> outline, string startAnchor, string endAnchor)
    {
        SpeechSegment? start = document.FirstForAnchor(startAnchor);
        SpeechSegment? end = document.Segments.LastOrDefault(s => s.AnchorId == endAnchor) ?? start;
        if (start is null || end is null)
        {
            return new NoteLocation(0, 0, null);
        }

        return new NoteLocation(start.Line, Math.Max(start.Line, end.EndLine), SectionOf(outline, start.Id));
    }

    /// <summary>"Guía › Instalación › Requisitos": la entrada del índice que contiene el segmento y las que la contienen.</summary>
    private static string? SectionOf(IReadOnlyList<OutlineEntry> outline, int segmentId)
    {
        int index = DocumentOutline.IndexOf(outline, segmentId);
        if (index < 0)
        {
            return null;
        }

        var path = new List<string> { outline[index].Text };
        int level = outline[index].Level;
        for (int i = index - 1; i >= 0; i--)
        {
            if (outline[i].Level < level)
            {
                path.Insert(0, outline[i].Text);
                level = outline[i].Level;
            }
        }

        return string.Join(" › ", path);
    }
}
