namespace Shoken.Core.Tts;

/// <summary>Fonemas de Kokoro: del español de España al de México, y cortes para no pasar el límite del modelo.</summary>
public static class KokoroPhonemes
{
    /// <summary>
    /// Kokoro fonetiza el español peninsular. En México no hay θ ("quince", "haz" se dicen con s: seseo) ni ʎ
    /// ("calle", "llave" se dicen como "y": yeísmo).
    /// </summary>
    public static string ToMexican(string phonemes) => phonemes.Replace('θ', 's').Replace('ʎ', 'ʝ');

    /// <summary>
    /// Parte los tokens en tramos de a lo sumo <paramref name="max"/>: primero tras la última puntuación que
    /// quepa, si no tras el último espacio, y si no a la fuerza. Cada tramo se sintetiza aparte.
    /// </summary>
    public static IReadOnlyList<int[]> Split(IReadOnlyList<int> tokens, int max, Func<int, bool> isPunctuation, Func<int, bool> isSpace)
    {
        var pieces = new List<int[]>();
        int start = 0;
        while (tokens.Count - start > max)
        {
            int cut = LastIndex(tokens, start, max, isPunctuation);
            if (cut < 0)
            {
                cut = LastIndex(tokens, start, max, isSpace);
            }

            int end = cut >= 0 ? cut + 1 : start + max;
            pieces.Add(Slice(tokens, start, end));
            start = end;
        }

        if (start < tokens.Count)
        {
            pieces.Add(Slice(tokens, start, tokens.Count));
        }

        return pieces;
    }

    private static int LastIndex(IReadOnlyList<int> tokens, int start, int max, Func<int, bool> matches)
    {
        for (int i = start + max - 1; i > start; i--)
        {
            if (matches(tokens[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int[] Slice(IReadOnlyList<int> tokens, int start, int end)
    {
        var slice = new int[end - start];
        for (int i = start; i < end; i++)
        {
            slice[i - start] = tokens[i];
        }

        return slice;
    }
}
