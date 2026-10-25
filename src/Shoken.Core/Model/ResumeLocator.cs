namespace Shoken.Core.Model;

/// <summary>
/// Dónde iba la lectura, descrito por el contenido del bloque y no solo por el número de segmento,
/// para retomarla en el mismo párrafo aunque el archivo haya cambiado.
/// </summary>
/// <param name="SegmentId">Id del segmento en el documento donde se tomó el punto.</param>
/// <param name="BlockKey">Huella del bloque (<see cref="SpeechSegment.BlockKey"/>), o null si no se conoce.</param>
/// <param name="Part">Índice del segmento dentro de su bloque (0 para el primero).</param>
public sealed record ResumePoint(int SegmentId, string? BlockKey, int Part)
{
    public static ResumePoint Of(SpeechDocument document, int segmentId)
    {
        if (segmentId < 0 || segmentId >= document.Segments.Count)
        {
            return new ResumePoint(segmentId, null, 0);
        }

        string key = document.Segments[segmentId].BlockKey;
        int first = segmentId;
        while (first > 0 && document.Segments[first - 1].BlockKey == key)
        {
            first--;
        }

        return new ResumePoint(segmentId, key, segmentId - first);
    }
}

public static class ResumeLocator
{
    /// <summary>
    /// Segmento de <paramref name="document"/> donde retomar: el bloque con la misma huella (el más cercano si
    /// está repetido) y la misma parte dentro de él. Si el párrafo ya no existe, el que ocupa su lugar.
    /// Devuelve -1 si el documento no tiene segmentos.
    /// </summary>
    public static int Find(SpeechDocument document, ResumePoint point)
    {
        var segments = document.Segments;
        if (segments.Count == 0)
        {
            return -1;
        }

        int fallback = Math.Clamp(point.SegmentId, 0, segments.Count - 1);
        if (point.BlockKey is null)
        {
            return fallback;
        }

        int bestFirst = -1;
        int bestParts = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].BlockKey != point.BlockKey || (i > 0 && segments[i - 1].BlockKey == point.BlockKey))
            {
                continue;
            }

            int parts = 1;
            while (i + parts < segments.Count && segments[i + parts].BlockKey == point.BlockKey)
            {
                parts++;
            }

            if (bestFirst < 0 || Math.Abs(i - point.SegmentId) < Math.Abs(bestFirst - point.SegmentId))
            {
                bestFirst = i;
                bestParts = parts;
            }
        }

        return bestFirst < 0 ? fallback : bestFirst + Math.Min(point.Part, bestParts - 1);
    }
}
