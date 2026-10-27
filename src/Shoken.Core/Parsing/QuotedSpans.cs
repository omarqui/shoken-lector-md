using System.Text;
using System.Text.RegularExpressions;
using Shoken.Core.Model;

namespace Shoken.Core.Parsing;

/// <summary>
/// Reconoce los tramos entre comillas y entre paréntesis de un bloque y los marca con <see cref="RunStyle.Quoted"/> o
/// <see cref="RunStyle.Aside"/> para que el SSML les dé su tono. Solo parte los tramos en esos bordes: el texto no cambia.
/// Si un tramo está dentro de otro, manda el de adentro: en «(pulsa «Guardar»)», «Guardar» suena como nombre.
/// </summary>
internal static class QuotedSpans
{
    private const RunStyle SpanStyles = RunStyle.Quoted | RunStyle.Aside;

    // Paréntesis que no son un aparte: (1), (a), (iv).
    private static readonly Regex Enumerator = new(@"^\s*(?:\d+|[A-Za-z]|[ivxlcdmIVXLCDM]{1,4})\s*$", RegexOptions.CultureInvariant);

    private const string OpeningContext = "([{«“¿¡—–-/";

    public static List<SpeechRun> Mark(List<SpeechRun> runs)
    {
        var text = new StringBuilder();
        var owner = new List<int>(); // tramo de origen de cada carácter
        for (int r = 0; r < runs.Count; r++)
        {
            text.Append(runs[r].Text);
            for (int i = 0; i < runs[r].Text.Length; i++)
            {
                owner.Add(r);
            }
        }

        List<(int Start, int End, RunStyle Kind)> spans = FindSpans(text.ToString(), i => runs[owner[i]].Style.HasFlag(RunStyle.Code));
        if (spans.Count == 0)
        {
            return runs;
        }

        // De afuera hacia adentro: el tramo interior pisa al exterior.
        var kinds = new RunStyle[text.Length];
        foreach ((int start, int end, RunStyle kind) in spans.OrderByDescending(s => s.End - s.Start))
        {
            Array.Fill(kinds, kind, start, end - start + 1);
        }

        var result = new List<SpeechRun>();
        int from = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || owner[i] != owner[from] || kinds[i] != kinds[from])
            {
                result.Add(new SpeechRun(text.ToString(from, i - from), runs[owner[from]].Style | kinds[from]));
                from = i;
            }
        }

        return result;
    }

    /// <summary>Junta lo que <see cref="Mark"/> separó: los tramos tal como estaban antes de marcarlos.</summary>
    public static IEnumerable<SpeechRun> Unmark(IEnumerable<SpeechRun> runs)
    {
        SpeechRun? pending = null;
        foreach (SpeechRun run in runs)
        {
            SpeechRun plain = run with { Style = run.Style & ~SpanStyles };
            if (pending is not null && pending.Style == plain.Style)
            {
                pending = pending with { Text = pending.Text + plain.Text };
                continue;
            }

            if (pending is not null)
            {
                yield return pending;
            }

            pending = plain;
        }

        if (pending is not null)
        {
            yield return pending;
        }
    }

    internal static List<(int Start, int End, RunStyle Kind)> FindSpans(string text, Func<int, bool> isCode)
    {
        var spans = new List<(int, int, RunStyle)>();
        var open = new List<(char Close, int Start)>();

        for (int i = 0; i < text.Length; i++)
        {
            if (isCode(i))
            {
                continue;
            }

            char c = text[i];
            switch (c)
            {
                case '«':
                    open.Add(('»', i));
                    break;
                case '“':
                    open.Add(('”', i));
                    break;
                case '(':
                    open.Add((')', i));
                    break;
                case '»' or '”' or ')':
                    Close(c, i);
                    break;
                case '"':
                    bool straightOpen = open.Count > 0 && open[^1].Close == '"';
                    if (IsDigit(text, i - 1) && !straightOpen)
                    {
                        break; // 5" (pulgadas)
                    }

                    if (straightOpen && ClosesStraight(text, i))
                    {
                        Close('"', i);
                    }
                    else if (OpensStraight(text, i))
                    {
                        open.Add(('"', i));
                    }
                    else if (ClosesStraight(text, i))
                    {
                        Close('"', i);
                    }

                    break;
            }
        }

        return spans;

        void Close(char close, int end)
        {
            int index = open.FindLastIndex(o => o.Close == close);
            if (index < 0)
            {
                return; // sin pareja
            }

            int start = open[index].Start;
            open.RemoveRange(index, open.Count - index); // los que quedaron abiertos adentro no tenían pareja

            string inner = text.Substring(start + 1, end - start - 1);
            if (inner.Trim().Length == 0 || (close == ')' && Enumerator.IsMatch(inner)))
            {
                return;
            }

            spans.Add((start, end, close == ')' ? RunStyle.Aside : RunStyle.Quoted));
        }
    }

    // La comilla recta abre al empezar o tras espacio o signo de apertura, y pegada a lo que sigue.
    private static bool OpensStraight(string text, int i) =>
        (i == 0 || char.IsWhiteSpace(text[i - 1]) || OpeningContext.Contains(text[i - 1]))
        && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]);

    // Y cierra pegada a lo anterior, al terminar o antes de espacio o puntuación.
    private static bool ClosesStraight(string text, int i) =>
        i > 0 && !char.IsWhiteSpace(text[i - 1])
        && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]) || char.IsPunctuation(text[i + 1]));

    private static bool IsDigit(string text, int i) => i >= 0 && char.IsDigit(text[i]);
}
