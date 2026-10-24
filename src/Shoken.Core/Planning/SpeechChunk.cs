using Shoken.Core.Model;

namespace Shoken.Core.Planning;

/// <summary>
/// Grupo de segmentos consecutivos que se sintetiza en una sola petición (un archivo de audio).
/// </summary>
public sealed class SpeechChunk
{
    public required int Index { get; init; }

    public required IReadOnlyList<SpeechSegment> Segments { get; init; }

    /// <summary>Silencio al inicio del audio, para conservar la pausa con el bloque anterior.</summary>
    public int LeadingPauseMs { get; init; }

    public int FirstSegmentId => Segments[0].Id;

    public int LastSegmentId => Segments[^1].Id;

    public int CharacterCount => Segments.Sum(s => s.PlainText.Length);

    public bool Contains(int segmentId) => segmentId >= FirstSegmentId && segmentId <= LastSegmentId;

    /// <summary>Pausa entre el segmento <paramref name="index"/>-1 y el <paramref name="index"/> de este bloque.</summary>
    public int GapBefore(int index) => index == 0
        ? LeadingPauseMs
        : Pauses.Gap(Segments[index - 1], Segments[index]);
}

public static class Pauses
{
    /// <summary>Pausa máxima entre dos segmentos, para que nunca parezca que la lectura se detuvo.</summary>
    public const int MaxGapMs = 2500;

    /// <summary>Pausas menores a esto no se emiten (la voz ya hace micro-pausas naturales).</summary>
    public const int MinGapMs = 50;

    public static int Gap(SpeechSegment previous, SpeechSegment current) =>
        Math.Min(MaxGapMs, previous.PauseAfterMs + current.PauseBeforeMs);
}
