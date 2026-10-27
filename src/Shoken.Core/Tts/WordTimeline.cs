using System.Globalization;
using System.Text;
using Shoken.Core.Model;
using Shoken.Core.Planning;

namespace Shoken.Core.Tts;

/// <summary>
/// Cuándo empieza cada palabra de los segmentos de un bloque de audio, para resaltar la que suena. Las palabras son
/// las del texto del segmento (<see cref="Tokenize"/>), que la página empareja con las que muestra. Los tiempos son
/// exactos donde el motor los dio (<see cref="AudioChunk.Words"/>, Azure) y, entre ellos o sin ellos, se reparten
/// según el largo de cada palabra. Nunca se pide audio para conseguirlos.
/// </summary>
public sealed class WordTimeline
{
    // Si no se sabe cuánto dura el último segmento: la misma velocidad que estima ReadingTimeline.
    private const double CharsPerSecond = 15;

    private readonly Dictionary<int, TimeSpan[]> _starts;

    private WordTimeline(Dictionary<int, TimeSpan[]> starts) => _starts = starts;

    /// <summary>Palabras del texto, sin los signos que las rodean; lo que no tiene letras ni números no cuenta.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var words = new List<string>();
        foreach (string piece in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = piece.Trim(TrimmedChars(piece));
            if (word.Any(char.IsLetterOrDigit))
            {
                words.Add(word);
            }
        }

        return words;
    }

    public static IReadOnlyList<string> Tokenize(SpeechSegment segment) => Tokenize(segment.PlainText);

    /// <param name="duration">Duración del audio si <see cref="AudioChunk.Duration"/> no la trae (la del reproductor).</param>
    public static WordTimeline Build(SpeechChunk chunk, AudioChunk audio, TimeSpan? duration = null)
    {
        TimeSpan? total = audio.Duration ?? duration;
        var starts = new Dictionary<int, TimeSpan[]>();
        for (int i = 0; i < chunk.Segments.Count; i++)
        {
            SpeechSegment segment = chunk.Segments[i];
            IReadOnlyList<string> words = Tokenize(segment);
            TimeSpan begin = audio.OffsetOf(segment.Id);
            TimeSpan end;
            if (i + 1 < chunk.Segments.Count)
            {
                TimeSpan next = audio.OffsetOf(chunk.Segments[i + 1].Id);
                end = next - TimeSpan.FromMilliseconds(Math.Max(0, chunk.GapBefore(i + 1)));
                end = end > begin ? end : next;
            }
            else
            {
                end = total is { } t && t > begin
                    ? t
                    : begin + TimeSpan.FromSeconds(segment.PlainText.Length / CharsPerSecond);
            }

            List<WordMark> exact = audio.Words.Where(w => w.SegmentId == segment.Id).ToList();
            starts[segment.Id] = Place(words, exact, begin, end > begin ? end : begin);
        }

        return new WordTimeline(starts);
    }

    /// <summary>Inicio de cada palabra del segmento (vacío si no es de este bloque).</summary>
    public IReadOnlyList<TimeSpan> StartsOf(int segmentId) =>
        _starts.TryGetValue(segmentId, out TimeSpan[]? starts) ? starts : [];

    /// <summary>Palabra del segmento que suena en <paramref name="position"/>, o -1 si todavía no empieza ninguna.</summary>
    public int WordAt(int segmentId, TimeSpan position)
    {
        if (!_starts.TryGetValue(segmentId, out TimeSpan[]? starts))
        {
            return -1;
        }

        int word = -1;
        for (int i = 0; i < starts.Length && starts[i] <= position; i++)
        {
            word = i;
        }

        return word;
    }

    /// <summary>
    /// Fija las palabras que coinciden con las del motor (en orden) y reparte las demás entre las fijas,
    /// según su largo. Sin palabras del motor, reparte todo el segmento.
    /// </summary>
    internal static TimeSpan[] Place(IReadOnlyList<string> words, IReadOnlyList<WordMark> exact, TimeSpan begin, TimeSpan end)
    {
        var fixedAt = new TimeSpan?[words.Count];
        int next = 0;
        for (int i = 0; i < words.Count && next < exact.Count; i++)
        {
            string word = Fold(words[i]);
            // Busca un poco más adelante: el motor puede partir una palabra («appsettings.json») o decir de más.
            for (int k = next; k < Math.Min(exact.Count, next + 4); k++)
            {
                string spoken = Fold(exact[k].Text);
                if (spoken.Length > 0 && (spoken == word || (spoken.Length >= 2 && word.StartsWith(spoken, StringComparison.Ordinal))))
                {
                    TimeSpan offset = exact[k].Offset;
                    fixedAt[i] = offset < begin ? begin : offset > end ? end : offset;
                    next = k + 1;
                    break;
                }
            }
        }

        var starts = new TimeSpan[words.Count];
        int previous = -1;
        TimeSpan previousTime = begin;
        for (int i = 0; i <= words.Count; i++)
        {
            if (i < words.Count && fixedAt[i] is null)
            {
                continue;
            }

            TimeSpan until = i < words.Count ? fixedAt[i]!.Value : end;
            if (until < previousTime)
            {
                until = previousTime; // el motor nunca retrocede; si una coincidencia lo hiciera, no se respeta
            }

            // Las palabras sin tiempo entre la fija anterior y esta (o el final) se reparten por su largo.
            int from = previous + 1;
            TimeSpan spanStart = previous < 0 ? begin : previousTime;
            double weight = 0;
            for (int j = previous < 0 ? 0 : previous; j < i; j++)
            {
                weight += words[j].Length + 1;
            }

            double done = previous < 0 ? 0 : words[previous].Length + 1;
            for (int j = from; j < i; j++)
            {
                starts[j] = spanStart + (until - spanStart) * (weight > 0 ? done / weight : 0);
                done += words[j].Length + 1;
            }

            if (i < words.Count)
            {
                starts[i] = until;
                previous = i;
                previousTime = until;
            }
        }

        return starts;
    }

    private static char[] TrimmedChars(string piece) =>
        piece.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray();

    /// <summary>Minúsculas, sin acentos y solo letras y números: como compara la página.</summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
