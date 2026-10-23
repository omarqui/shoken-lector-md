using LectorMd.Core.Model;

namespace LectorMd.Core.Planning;

public sealed class ChunkingOptions
{
    /// <summary>Tamaño objetivo (caracteres de texto) de cada bloque de audio.</summary>
    public int MaxChars { get; init; } = 700;

    /// <summary>Los primeros bloques son más cortos para que la lectura empiece rápido.</summary>
    public int[] WarmupMaxChars { get; init; } = [350, 550];

    /// <summary>Un encabezado de nivel ≤ este valor inicia bloque nuevo...</summary>
    public int SectionHeadingLevel { get; init; } = 3;

    /// <summary>...siempre que el bloque actual ya tenga al menos estos caracteres.</summary>
    public int MinCharsBeforeSectionBreak { get; init; } = 250;

    /// <summary>No se cierra un bloque con menos de esto aunque el siguiente segmento sea largo.</summary>
    public int MinChunkChars { get; init; } = 150;

    /// <summary>Milisegundos que el reproductor tarda en pasar de un archivo al siguiente.</summary>
    public int TransitionMs { get; init; } = 150;
}

/// <summary>
/// Agrupa los segmentos en bloques de audio. El plan es determinista (no depende de dónde empiece
/// a leer el usuario) para que la caché de audio se reutilice y no se gaste cuota de nuevo.
/// </summary>
public static class ChunkPlanner
{
    public static IReadOnlyList<SpeechChunk> Plan(IReadOnlyList<SpeechSegment> segments, ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();
        var chunks = new List<SpeechChunk>();
        var current = new List<SpeechSegment>();
        int chars = 0;

        foreach (SpeechSegment segment in segments)
        {
            int limit = chunks.Count < options.WarmupMaxChars.Length ? options.WarmupMaxChars[chunks.Count] : options.MaxChars;
            int length = segment.PlainText.Length;

            bool sectionBreak = segment.IsHeading
                                && segment.HeadingLevel <= options.SectionHeadingLevel
                                && chars >= options.MinCharsBeforeSectionBreak;
            bool tooLong = chars + length > limit && chars >= options.MinChunkChars;

            if (current.Count > 0 && (sectionBreak || tooLong))
            {
                Flush();
            }

            current.Add(segment);
            chars += length;
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            int leading = 0;
            int firstIndex = current[0].Id;
            if (firstIndex > 0 && firstIndex - 1 < segments.Count && segments[firstIndex - 1].Id == firstIndex - 1)
            {
                leading = Math.Max(0, Pauses.Gap(segments[firstIndex - 1], current[0]) - options.TransitionMs);
            }

            chunks.Add(new SpeechChunk { Index = chunks.Count, Segments = current, LeadingPauseMs = leading });
            current = [];
            chars = 0;
        }
    }

    /// <summary>Índice del bloque que contiene a cada segmento.</summary>
    public static int[] BuildSegmentIndex(IReadOnlyList<SpeechChunk> chunks, int segmentCount)
    {
        var map = new int[segmentCount];
        foreach (SpeechChunk chunk in chunks)
        {
            foreach (SpeechSegment segment in chunk.Segments)
            {
                if (segment.Id >= 0 && segment.Id < segmentCount)
                {
                    map[segment.Id] = chunk.Index;
                }
            }
        }

        return map;
    }
}
