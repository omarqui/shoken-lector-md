using System.Globalization;
using System.Text;
using Shoken.Core.Model;
using Shoken.Core.Planning;
using Shoken.Core.Text;

namespace Shoken.Core.Ssml;

/// <summary>
/// Ajustes de prosodia (en %) que traducen el formato Markdown a entonación. Un valor 0 no emite etiqueta:
/// Azure cobra también el marcado, así que por defecto solo se marcan encabezados y negritas
/// (las citas y alertas se distinguen con pausas, que son más baratas y se notan más).
/// </summary>
public sealed class ProsodyProfile
{
    public int HeadingRate { get; init; } = -6;
    public int HeadingPitch { get; init; } = -3;
    public int QuoteRate { get; init; }
    public int AlertRate { get; init; }
    public int AlertPitch { get; init; }
    public int StrongRate { get; init; } = -4;
    public int StrongPitch { get; init; } = 6;
    public int EmphasisRate { get; init; }
    public int EmphasisPitch { get; init; }

    /// <summary>Entre comillas: un nombre («Guardar», "Usar"). Solo tono, sin pausas, que cuestan más.</summary>
    public int QuotedRate { get; init; }
    public int QuotedPitch { get; init; } = 8;

    /// <summary>Entre paréntesis: un aparte, más grave y algo más rápido.</summary>
    public int AsideRate { get; init; } = 6;
    public int AsidePitch { get; init; } = -6;

    /// <summary>Velocidad y tono de un segmento entero según su tipo (0 = sin cambio).</summary>
    internal (int Rate, int Pitch) ForKind(SegmentKind kind) => kind switch
    {
        SegmentKind.Heading => (HeadingRate, HeadingPitch),
        SegmentKind.Quote => (QuoteRate, 0),
        SegmentKind.AlertLabel => (AlertRate, AlertPitch),
        _ => (0, 0),
    };

    /// <summary>
    /// Velocidad y tono de un tramo (0 = sin cambio). Se aplica uno solo, para no anidar prosodia:
    /// negrita, luego comillas o paréntesis, luego cursiva.
    /// </summary>
    internal (int Rate, int Pitch) ForRun(RunStyle style) =>
        style.HasFlag(RunStyle.Strong) ? (StrongRate, StrongPitch)
        : style.HasFlag(RunStyle.Quoted) ? (QuotedRate, QuotedPitch)
        : style.HasFlag(RunStyle.Aside) ? (AsideRate, AsidePitch)
        : style.HasFlag(RunStyle.Emphasis) ? (EmphasisRate, EmphasisPitch)
        : (0, 0);

    /// <summary>
    /// Si un tramo con este estilo lleva prosodia. Los de comillas o paréntesis que solo tienen signos (los «» que
    /// quedan sueltos alrededor de una negrita) no: Azure cobraría la etiqueta por algo que no se dice.
    /// </summary>
    internal bool Styles(RunStyle style, string text)
    {
        (int rate, int pitch) = ForRun(style);
        if (rate == 0 && pitch == 0)
        {
            return false;
        }

        bool onlySpan = (style & (RunStyle.Strong | RunStyle.Emphasis)) == 0;
        return !onlySpan || text.Any(char.IsLetterOrDigit);
    }
}

/// <param name="Ssml">Documento SSML completo.</param>
/// <param name="BillableCharacters">
/// Caracteres que Azure factura: todo lo que hay dentro de &lt;voice&gt; (texto y etiquetas),
/// excepto las propias etiquetas &lt;speak&gt; y &lt;voice&gt;.
/// </param>
public sealed record SsmlDocument(string Ssml, int BillableCharacters);

/// <summary>
/// Genera SSML para Azure. Cada segmento lleva un &lt;bookmark mark="i"/&gt; con su índice dentro del bloque,
/// de modo que Azure devuelve el instante exacto en que empieza cada uno (para resaltarlo).
/// Se usa solo el marcado indispensable porque Azure también cobra las etiquetas.
/// </summary>
public sealed class SsmlBuilder(SpeechTextNormalizer normalizer, ProsodyProfile? profile = null)
{
    private readonly ProsodyProfile _profile = profile ?? new ProsodyProfile();

    public SsmlDocument Build(SpeechChunk chunk, string voiceName, int ratePercent = 0, string language = "es-MX")
    {
        var body = new StringBuilder();
        for (int i = 0; i < chunk.Segments.Count; i++)
        {
            int gap = chunk.GapBefore(i);
            if (gap >= Pauses.MinGapMs)
            {
                body.Append("<break time=\"").Append(gap.ToString(CultureInfo.InvariantCulture)).Append("ms\"/>");
            }

            body.Append("<bookmark mark=\"").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
            AppendSegment(body, chunk.Segments[i], ratePercent);
            body.Append(' ');
        }

        string inner = body.ToString().TrimEnd();
        string ssml =
            $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{Escape(language)}\">" +
            $"<voice name=\"{Escape(voiceName)}\">{inner}</voice></speak>";
        return new SsmlDocument(ssml, inner.Length);
    }

    private void AppendSegment(StringBuilder body, SpeechSegment segment, int ratePercent)
    {
        (int kindRate, int kindPitch) = _profile.ForKind(segment.Kind);

        int rate = CombinePercent(ratePercent, kindRate);
        bool wrap = rate != 0 || kindPitch != 0;
        if (wrap)
        {
            OpenProsody(body, rate, kindPitch);
        }

        // Normaliza cada tramo y asegura que el segmento cierre con puntuación (entonación descendente).
        var texts = segment.Runs.Select(r => normalizer.Normalize(r.Text)).ToList();
        int last = texts.FindLastIndex(t => !string.IsNullOrWhiteSpace(t));
        if (last >= 0)
        {
            texts[last] = SpeechTextNormalizer.EnsureTerminalPunctuation(texts[last]);
        }

        for (int i = 0; i < texts.Count; i++)
        {
            string text = texts[i];
            if (text.Length == 0)
            {
                continue;
            }

            RunStyle style = segment.Runs[i].Style;
            (int runRate, int runPitch) = _profile.ForRun(style);
            bool styled = !segment.IsHeading && !string.IsNullOrWhiteSpace(text) && _profile.Styles(style, text);
            if (!styled)
            {
                body.Append(Escape(text));
                continue;
            }

            // Los espacios de los extremos quedan fuera de la prosodia.
            string core = text.Trim();
            if (text[0] == ' ')
            {
                body.Append(' ');
            }

            OpenProsody(body, runRate, runPitch);
            body.Append(Escape(core)).Append("</prosody>");
            if (text[^1] == ' ')
            {
                body.Append(' ');
            }
        }

        if (wrap)
        {
            body.Append("</prosody>");
        }
    }

    private static void OpenProsody(StringBuilder body, int rate, int pitch)
    {
        body.Append("<prosody");
        if (rate != 0)
        {
            body.Append(" rate=\"").Append(FormatPercent(rate)).Append('"');
        }

        if (pitch != 0)
        {
            body.Append(" pitch=\"").Append(FormatPercent(pitch)).Append('"');
        }

        body.Append('>');
    }

    internal static int CombinePercent(int a, int b) =>
        (int)Math.Round(((1 + a / 100.0) * (1 + b / 100.0) - 1) * 100, MidpointRounding.AwayFromZero);

    private static string FormatPercent(int value) =>
        (value >= 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture) + "%";

    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    // Caracteres de control no válidos en XML.
                    if (c >= ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
