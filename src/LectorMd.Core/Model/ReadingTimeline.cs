using LectorMd.Core.Planning;
using LectorMd.Core.Tts;

namespace LectorMd.Core.Model;

/// <summary>Un tramo de la línea de tiempo: un título del índice y todo lo que hay hasta el siguiente.</summary>
public sealed record TimelineChapter(string Title, string? AnchorId, int FirstSegmentId, TimeSpan Start, TimeSpan Duration, bool IsEstimated);

/// <summary>
/// Duración del documento por segmento y por capítulo. Usa la duración real de los bloques que ya están
/// en la caché y estima el resto, para mostrar cuánto falta antes de haber generado todo el audio.
/// </summary>
public sealed class ReadingTimeline
{
    /// <summary>Velocidad de lectura estimada en español, sin contar las pausas.</summary>
    public const double CharactersPerSecond = 15;

    private readonly TimeSpan[] _starts;
    private readonly TimeSpan[] _durations;

    private ReadingTimeline(TimeSpan[] starts, TimeSpan[] durations, IReadOnlyList<TimelineChapter> chapters, TimeSpan total)
    {
        _starts = starts;
        _durations = durations;
        Chapters = chapters;
        Total = total;
    }

    public IReadOnlyList<TimelineChapter> Chapters { get; }

    public TimeSpan Total { get; }

    /// <param name="cached">El audio ya generado de un bloque, o null si todavía no existe.</param>
    public static ReadingTimeline Build(SpeechDocument document, IReadOnlyList<SpeechChunk> chunks, Func<SpeechChunk, AudioChunk?> cached)
    {
        int count = document.Segments.Count;
        var durations = new TimeSpan[count];
        var estimated = new bool[count];
        for (int i = 0; i < count; i++)
        {
            durations[i] = Estimate(document.Segments[i]);
            estimated[i] = true;
        }

        foreach (SpeechChunk chunk in chunks)
        {
            if (cached(chunk) is not { Duration: { } total, Marks.Count: > 0 } audio)
            {
                continue;
            }

            var marks = audio.Marks.OrderBy(m => m.Offset).ToList();
            for (int i = 0; i < marks.Count; i++)
            {
                int id = marks[i].SegmentId;
                if (id < 0 || id >= count)
                {
                    continue;
                }

                // El primer segmento se queda también con el silencio inicial del bloque.
                TimeSpan start = i == 0 ? TimeSpan.Zero : marks[i].Offset;
                TimeSpan end = i + 1 < marks.Count ? marks[i + 1].Offset : total;
                durations[id] = end > start ? end - start : TimeSpan.Zero;
                estimated[id] = false;
            }
        }

        var starts = new TimeSpan[count];
        TimeSpan elapsed = TimeSpan.Zero;
        for (int i = 0; i < count; i++)
        {
            starts[i] = elapsed;
            elapsed += durations[i];
        }

        return new ReadingTimeline(starts, durations, BuildChapters(document, starts, durations, estimated, elapsed), elapsed);
    }

    public TimeSpan StartOf(int segmentId) => InRange(segmentId) ? _starts[segmentId] : TimeSpan.Zero;

    public TimeSpan DurationOf(int segmentId) => InRange(segmentId) ? _durations[segmentId] : TimeSpan.Zero;

    /// <summary>Índice del capítulo que contiene el segmento.</summary>
    public int ChapterIndexOf(int segmentId)
    {
        int index = 0;
        for (int i = 0; i < Chapters.Count && Chapters[i].FirstSegmentId <= segmentId; i++)
        {
            index = i;
        }

        return index;
    }

    /// <summary>Tiempo transcurrido desde el inicio del documento en un punto de un segmento.</summary>
    public TimeSpan ElapsedAt(int segmentId, TimeSpan intoSegment)
    {
        TimeSpan duration = DurationOf(segmentId);
        TimeSpan clamped = intoSegment < TimeSpan.Zero ? TimeSpan.Zero : intoSegment > duration ? duration : intoSegment;
        return StartOf(segmentId) + clamped;
    }

    private bool InRange(int segmentId) => segmentId >= 0 && segmentId < _durations.Length;

    private static TimeSpan Estimate(SpeechSegment segment) =>
        TimeSpan.FromSeconds(segment.PlainText.Length / CharactersPerSecond)
        + TimeSpan.FromMilliseconds(segment.PauseBeforeMs + segment.PauseAfterMs);

    private static List<TimelineChapter> BuildChapters(SpeechDocument document, TimeSpan[] starts, TimeSpan[] durations,
        bool[] estimated, TimeSpan total)
    {
        int count = durations.Length;
        IReadOnlyList<OutlineEntry> outline = DocumentOutline.Build(document);

        // (título, ancla, primer segmento) de cada tramo; lo anterior al primer título es "Inicio" si dura algo.
        var heads = new List<(string Title, string? Anchor, int First)>();
        if (outline.Count == 0)
        {
            heads.Add(("Documento", null, 0));
        }
        else
        {
            if (outline[0].SegmentId > 0 && starts[outline[0].SegmentId] > TimeSpan.Zero)
            {
                heads.Add(("Inicio", null, 0));
            }

            heads.AddRange(outline.Select(e => (e.Text, (string?)e.AnchorId, e.SegmentId)));
        }

        var chapters = new List<TimelineChapter>(heads.Count);
        for (int i = 0; i < heads.Count; i++)
        {
            int first = heads[i].First;
            int next = i + 1 < heads.Count ? heads[i + 1].First : count;
            TimeSpan start = first < count ? starts[first] : total;
            TimeSpan end = next < count ? starts[next] : total;
            bool isEstimated = Enumerable.Range(first, Math.Max(0, next - first)).Any(id => estimated[id]);
            chapters.Add(new TimelineChapter(heads[i].Title, heads[i].Anchor, first, start, end - start, isEstimated));
        }

        return chapters;
    }
}
