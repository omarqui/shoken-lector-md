namespace Shoken.Core.Model;

/// <summary>Un título del índice. <see cref="Indent"/> cuenta desde el nivel más alto que tenga el documento.</summary>
public sealed record OutlineEntry(int SegmentId, string AnchorId, int Level, int Indent, string Text);

/// <summary>Estados del botón de contraer del índice, en el orden en que los recorre.</summary>
public enum OutlineFold
{
    /// <summary>Los de primer nivel con sus subtítulos directos; lo más hondo, contraído. Es como abre cada documento.</summary>
    FirstLevel,

    /// <summary>Todo expandido.</summary>
    None,
}

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

    /// <summary>Si la entrada tiene subtítulos debajo (la siguiente es de un nivel más hondo).</summary>
    public static bool HasChildren(IReadOnlyList<OutlineEntry> outline, int index) =>
        index + 1 < outline.Count && outline[index + 1].Level > outline[index].Level;

    /// <summary>Primera entrada que ya no es parte de la sección (la siguiente de igual o mayor jerarquía), o el total.</summary>
    public static int SectionEnd(IReadOnlyList<OutlineEntry> outline, int index)
    {
        int end = index + 1;
        while (end < outline.Count && outline[end].Level > outline[index].Level)
        {
            end++;
        }

        return end;
    }

    /// <summary>Entradas que se ven con las secciones contraídas (por su ancla): sus subtítulos se ocultan.</summary>
    public static IReadOnlyList<int> VisibleRows(IReadOnlyList<OutlineEntry> outline, IReadOnlySet<string> collapsedAnchors)
    {
        var rows = new List<int>(outline.Count);
        for (int i = 0; i < outline.Count; i++)
        {
            rows.Add(i);
            if (collapsedAnchors.Contains(outline[i].AnchorId))
            {
                i = SectionEnd(outline, i) - 1;
            }
        }

        return rows;
    }

    /// <summary>Títulos que quedan contraídos (por su ancla) en cada estado del botón.</summary>
    public static IReadOnlySet<string> Folded(IReadOnlyList<OutlineEntry> outline, OutlineFold fold) =>
        outline
            .Where((entry, i) => fold == OutlineFold.FirstLevel && HasChildren(outline, i) && entry.Indent >= 1)
            .Select(entry => entry.AnchorId)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// El siguiente estado del botón: primer nivel ↔ expandir todo. Si los dos se verían igual queda uno solo;
    /// desde un estado hecho a mano, va al primer nivel.
    /// </summary>
    public static OutlineFold NextFold(IReadOnlyList<OutlineEntry> outline, IReadOnlySet<string> collapsedAnchors)
    {
        OutlineFold[] order = [OutlineFold.FirstLevel, OutlineFold.None];
        List<OutlineFold> cycle = order
            .Where((fold, i) => i == 0 || !Folded(outline, fold).SetEquals(Folded(outline, order[i - 1])))
            .ToList();
        int current = cycle.FindIndex(fold => Folded(outline, fold).SetEquals(collapsedAnchors));
        return cycle[(current + 1) % cycle.Count];
    }

    /// <summary>
    /// Fila que muestra una entrada: la suya, o la del título contraído que la oculta. -1 para "antes del primer título".
    /// </summary>
    public static int RowOf(IReadOnlyList<int> visibleRows, int entryIndex)
    {
        int row = -1;
        for (int r = 0; r < visibleRows.Count && visibleRows[r] <= entryIndex; r++)
        {
            row = r;
        }

        return row;
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
