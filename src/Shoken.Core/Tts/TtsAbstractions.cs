using Shoken.Core.Planning;

namespace Shoken.Core.Tts;

/// <summary>Instante (dentro del audio de un bloque) en que empieza un segmento.</summary>
public sealed record SegmentMark(int SegmentId, TimeSpan Offset);

/// <summary>Audio sintetizado de un bloque, listo para reproducir.</summary>
public sealed class AudioChunk
{
    public required string FilePath { get; init; }

    /// <summary>Marcas ordenadas por tiempo, una por segmento del bloque.</summary>
    public required IReadOnlyList<SegmentMark> Marks { get; init; }

    public required string EngineName { get; init; }

    public bool FromCache { get; init; }

    /// <summary>Duración total del audio; null en entradas de caché anteriores a que se guardara.</summary>
    public TimeSpan? Duration { get; init; }

    public TimeSpan OffsetOf(int segmentId) =>
        Marks.FirstOrDefault(m => m.SegmentId == segmentId)?.Offset ?? TimeSpan.Zero;

    /// <summary>Segmento que suena en <paramref name="position"/>.</summary>
    public int SegmentAt(TimeSpan position)
    {
        if (Marks.Count == 0)
        {
            return -1;
        }

        int id = Marks[0].SegmentId;
        foreach (SegmentMark mark in Marks)
        {
            if (mark.Offset > position)
            {
                break;
            }

            id = mark.SegmentId;
        }

        return id;
    }
}

public interface ITtsEngine
{
    string Name { get; }

    bool IsConfigured { get; }

    /// <summary>Devuelve el audio si ya está en caché, sin sintetizar ni gastar cuota.</summary>
    AudioChunk? TryGetCached(SpeechChunk chunk);

    Task<AudioChunk> SynthesizeAsync(SpeechChunk chunk, CancellationToken cancellationToken);
}

/// <summary>Motor de la nube que cobra por carácter: cuánto costaría sintetizar un bloque, sin pedirlo.</summary>
public interface IBillableEngine
{
    int BillableCharacters(SpeechChunk chunk);
}

public enum TtsFailureKind
{
    NotConfigured,
    Authentication,
    /// <summary>Azure respondió que la cuota se agotó.</summary>
    QuotaExceeded,
    /// <summary>Se alcanzó el límite mensual configurado en la app (Azure no se llegó a llamar).</summary>
    LocalLimitReached,
    RateLimited,
    Network,
    EngineCrashed,
    Other,
}

public sealed class TtsException(TtsFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TtsFailureKind Kind { get; } = kind;
}
