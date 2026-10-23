namespace LectorMd.Core.Model;

/// <summary>Un título del índice. <see cref="Indent"/> cuenta desde el nivel más alto que tenga el documento.</summary>
public sealed record OutlineEntry(int SegmentId, string AnchorId, int Level, int Indent, string Text);

/// <summary>Índice de títulos de un documento, para navegarlo y marcar la sección que se está leyendo.</summary>
public static class DocumentOutline
{
    public const int DefaultMaxLevel = 3;

    public static IReadOnlyList<OutlineEntry> Build(SpeechDocument document, int maxLevel = DefaultMaxLevel)
    {
        // Un título largo puede partirse en varios segmentos con el mismo ancla: cuenta como uno.
        var headings = document.Segments
            .Where(s => s.IsHeading && s.HeadingLevel <= maxLevel)
            .GroupBy(s => s.AnchorId, StringComparer.Ordinal)
            .Select(g => (First: g.First(), Text: string.Join(" ", g.Select(s => s.PlainText.Trim()))))
            .Where(h => h.Text.Length > 0)
            .ToList();

        if (headings.Count == 0)
        {
            return [];
        }

        int top = headings.Min(h => h.First.HeadingLevel);
        return headings
            .Select(h => new OutlineEntry(h.First.Id, h.First.AnchorId, h.First.HeadingLevel, h.First.HeadingLevel - top, h.Text))
            .ToList();
    }

    /// <summary>Índice de la entrada cuya sección contiene el segmento, o -1 si está antes del primer título.</summary>
    public static int IndexOf(IReadOnlyList<OutlineEntry> outline, int segmentId)
    {
        int index = -1;
        for (int i = 0; i < outline.Count && outline[i].SegmentId <= segmentId; i++)
        {
            index = i;
        }

        return index;
    }

    /// <summary>
    /// Resultados de una búsqueda por entrada del índice. Recibe cuántos hay en cada bloque (su <c>data-seg</c>);
    /// los de antes del primer título y los de anclas que no son del documento no se cuentan.
    /// </summary>
    public static IReadOnlyList<int> CountMatches(
        IReadOnlyList<OutlineEntry> outline, SpeechDocument document, IReadOnlyDictionary<string, int> matchesByAnchor)
    {
        var counts = new int[outline.Count];
        foreach ((string anchor, int matches) in matchesByAnchor)
        {
            if (document.FirstForAnchor(anchor) is { } segment && IndexOf(outline, segment.Id) is var index and >= 0)
            {
                counts[index] += matches;
            }
        }

        return counts;
    }
}
