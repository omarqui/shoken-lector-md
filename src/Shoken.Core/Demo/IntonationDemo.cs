using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shoken.Core.Model;
using Shoken.Core.Parsing;
using Shoken.Core.Planning;
using Shoken.Core.Ssml;
using Shoken.Core.Text;

namespace Shoken.Core.Demo;

/// <summary>Un cambio de velocidad y de tono, en %, como los de <see cref="ProsodyProfile"/>.</summary>
public sealed record ProsodyChange(int RatePercent, int PitchPercent);

/// <summary>Lo que hace Shoken con un segmento de la demo, para explicarlo junto al texto.</summary>
/// <param name="PauseBeforeMs">Pausa que Shoken pide antes del segmento (0 si no pide ninguna).</param>
/// <param name="Prosody">Cambio de velocidad y tono del segmento entero (los títulos), o null.</param>
/// <param name="Strong">Frases en negrita que cambian de tono (vacío si no hay o el perfil no las marca).</param>
/// <param name="StrongProsody">El cambio de esas negritas, o null si no hay.</param>
/// <param name="Quoted">Nombres entre comillas que cambian de tono (vacío si no hay).</param>
/// <param name="QuotedProsody">El cambio de esos nombres, o null si no hay.</param>
/// <param name="Asides">Apartes entre paréntesis que cambian de tono (vacío si no hay).</param>
/// <param name="AsideProsody">El cambio de esos apartes, o null si no hay.</param>
/// <param name="AddsPeriod">Shoken le agrega un punto al final para que la voz cierre la frase.</param>
/// <param name="ReadsNumber">Elemento de una lista numerada: se lee su número.</param>
/// <param name="Announces">Texto que agrega Shoken y no se ve como tal en el Markdown (la etiqueta de una alerta), o null.</param>
public sealed record DemoSegment(
    int Id,
    string AnchorId,
    SegmentKind Kind,
    int HeadingLevel,
    string Text,
    int PauseBeforeMs,
    ProsodyChange? Prosody,
    IReadOnlyList<string> Strong,
    ProsodyChange? StrongProsody,
    IReadOnlyList<string> Quoted,
    ProsodyChange? QuotedProsody,
    IReadOnlyList<string> Asides,
    ProsodyChange? AsideProsody,
    bool AddsPeriod,
    bool ReadsNumber,
    string? Announces);

/// <summary>El texto de la demo ya analizado y repartido en bloques, con el normalizador que le corresponde.</summary>
public sealed record DemoPlan(SpeechDocument Document, IReadOnlyList<SpeechChunk> Chunks, SpeechTextNormalizer Normalizer);

/// <summary>Una de las dos lecturas grabadas: su archivo, cuánto dura y cuándo empieza cada segmento.</summary>
/// <param name="Marks">Inicio de cada segmento, en segundos, por su <see cref="SpeechSegment.Id"/>.</param>
/// <param name="Fingerprints">Huella del SSML de cada bloque, para saber si la grabación quedó vieja.</param>
public sealed record DemoTake(string File, double DurationSeconds, IReadOnlyList<double> Marks, IReadOnlyList<string> Fingerprints, int BillableCharacters);

/// <summary>La grabación incluida en la app (la genera <c>empaquetado/generar-demo.cs</c>).</summary>
/// <param name="TextCharacters">Caracteres facturables de texto en la versión con Shoken.</param>
/// <param name="IntonationCharacters">Caracteres facturables de pausas y prosodia en la versión con Shoken.</param>
public sealed record DemoRecording(string Voice, DemoTake Con, DemoTake Sin, int TextCharacters, int IntonationCharacters);

/// <summary>Cuánto del SSML facturable es texto, marcas de resaltado, pausas y prosodia.</summary>
public sealed record SsmlCost(int Text, int Bookmarks, int Pauses, int Prosody)
{
    public int Intonation => Pauses + Prosody;
}

/// <summary>
/// Demo de entonación: el mismo texto leído con la entonación de Shoken y sin ella. Aquí está lo que no depende de la
/// interfaz: el texto, qué hace Shoken con cada segmento, la versión plana y los datos de la grabación incluida.
/// </summary>
public static class IntonationDemo
{
    /// <summary>Voz de la grabación incluida.</summary>
    public const string RecordingVoice = "es-MX-DaliaNeural";

    private static readonly Regex NumberedItem = new(@"^\s*\d+[.)]\s", RegexOptions.CultureInvariant);
    private static readonly Regex BookmarkTag = new("<bookmark[^>]*/>", RegexOptions.CultureInvariant);
    private static readonly Regex BreakTag = new("<break[^>]*/>", RegexOptions.CultureInvariant);
    private static readonly Regex ProsodyTag = new("</?prosody[^>]*>", RegexOptions.CultureInvariant);

    private static readonly Lazy<string> MarkdownText = new(() => ReadResource("demo.md")
        ?? throw new InvalidOperationException("Falta el texto de la demo (recurso demo.md)."));

    private static readonly Lazy<DemoRecording?> RecordingData = new(() =>
        ReadResource("demo-grabacion.json") is { } json ? ParseRecording(json) : null);

    /// <summary>El Markdown que lee la demo.</summary>
    public static string Markdown => MarkdownText.Value;

    /// <summary>La grabación incluida, o null si todavía no se generó.</summary>
    public static DemoRecording? Recording => RecordingData.Value;

    /// <summary>El plan de la grabación: parser y diccionario por omisión, para que no dependa del usuario.</summary>
    public static DemoPlan RecordedPlan()
    {
        SpeechDocument document = new MarkdownSpeechParser().Parse(Markdown);
        return new DemoPlan(document, ChunkPlanner.Plan(document.Segments), new SpeechTextNormalizer());
    }

    /// <summary>Qué hace Shoken con cada segmento: pausa antes, tono, punto agregado, número y texto anunciado.</summary>
    public static IReadOnlyList<DemoSegment> Describe(DemoPlan plan, ProsodyProfile? profile = null)
    {
        profile ??= new ProsodyProfile();
        var pauses = new Dictionary<int, int>();
        foreach (SpeechChunk chunk in plan.Chunks)
        {
            for (int i = 0; i < chunk.Segments.Count; i++)
            {
                int gap = chunk.GapBefore(i);
                pauses[chunk.Segments[i].Id] = gap >= Pauses.MinGapMs ? gap : 0;
            }
        }

        (int strongRate, int strongPitch) = profile.ForRun(RunStyle.Strong);
        ProsodyChange? strongProsody = strongRate != 0 || strongPitch != 0 ? new ProsodyChange(strongRate, strongPitch) : null;

        return plan.Document.Segments.Select(segment =>
        {
            (int rate, int pitch) = profile.ForKind(segment.Kind);
            IReadOnlyList<string> strong = segment.IsHeading || strongProsody is null
                ? []
                : segment.Runs.Where(r => r.Style.HasFlag(RunStyle.Strong) && !string.IsNullOrWhiteSpace(r.Text)).Select(r => r.Text.Trim()).ToList();
            IReadOnlyList<string> quoted = Phrases(segment, profile, RunStyle.Quoted);
            IReadOnlyList<string> asides = Phrases(segment, profile, RunStyle.Aside);
            return new DemoSegment(
                segment.Id,
                segment.AnchorId,
                segment.Kind,
                segment.HeadingLevel,
                segment.PlainText,
                pauses.GetValueOrDefault(segment.Id),
                rate != 0 || pitch != 0 ? new ProsodyChange(rate, pitch) : null,
                strong,
                strong.Count > 0 ? strongProsody : null,
                quoted,
                quoted.Count > 0 ? Change(profile.ForRun(RunStyle.Quoted)) : null,
                asides,
                asides.Count > 0 ? Change(profile.ForRun(RunStyle.Aside)) : null,
                AddsPeriod(segment, plan.Normalizer),
                segment.Kind == SegmentKind.ListItem && NumberedItem.IsMatch(segment.PlainText),
                segment.Kind == SegmentKind.AlertLabel ? segment.PlainText.Trim().TrimEnd('.') : null);
        }).ToList();
    }

    private static ProsodyChange Change((int Rate, int Pitch) change) => new(change.Rate, change.Pitch);

    /// <summary>
    /// Los tramos seguidos a los que <see cref="SsmlBuilder"/> les da el tono de <paramref name="kind"/> (comillas o
    /// paréntesis), con sus mismas reglas: la negrita manda y los tramos que solo tienen signos no cuentan.
    /// </summary>
    private static IReadOnlyList<string> Phrases(SpeechSegment segment, ProsodyProfile profile, RunStyle kind)
    {
        var phrases = new List<string>();
        if (segment.IsHeading)
        {
            return phrases;
        }

        var current = new StringBuilder();
        foreach (SpeechRun run in segment.Runs)
        {
            bool toned = run.Style.HasFlag(kind) && !run.Style.HasFlag(RunStyle.Strong)
                && !string.IsNullOrWhiteSpace(run.Text) && profile.Styles(run.Style, run.Text);
            if (toned)
            {
                current.Append(run.Text);
            }
            else if (current.Length > 0)
            {
                phrases.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            phrases.Add(current.ToString().Trim());
        }

        return phrases;
    }

    // Lo mismo que hace SsmlBuilder con el último tramo del segmento.
    private static bool AddsPeriod(SpeechSegment segment, SpeechTextNormalizer normalizer)
    {
        string? last = segment.Runs.Select(r => normalizer.Normalize(r.Text)).LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return last is not null && SpeechTextNormalizer.EnsureTerminalPunctuation(last).TrimEnd() != last.TrimEnd();
    }

    /// <summary>
    /// La versión sin entonación para cualquier motor: un solo bloque con un solo párrafo, sin estilos ni pausas, con el
    /// texto de todos los segmentos unido por espacios. Va sin normalizar porque el motor lo normaliza.
    /// </summary>
    public static SpeechChunk FlatChunk(SpeechDocument document)
    {
        string text = string.Join(' ', document.Segments.Select(s => s.PlainText.Trim()).Where(t => t.Length > 0));
        var segment = new SpeechSegment
        {
            Id = 0,
            AnchorId = "demo-sin-entonacion",
            Kind = SegmentKind.Paragraph,
            Runs = [new SpeechRun(text)],
        };
        return new SpeechChunk { Index = 0, Segments = [segment] };
    }

    /// <summary>
    /// La versión sin entonación de la grabación: el texto de cada segmento normalizado y unido por espacios, sin
    /// pausas, sin prosodia y sin el punto que agrega Shoken. Lleva un &lt;bookmark&gt; por segmento (no suena) para
    /// tener el inicio exacto de cada uno.
    /// </summary>
    public static SsmlDocument FlatSsml(DemoPlan plan, string voiceName, string language = "es-MX")
    {
        var body = new StringBuilder();
        foreach (SpeechSegment segment in plan.Document.Segments)
        {
            string text = string.Concat(segment.Runs.Select(r => plan.Normalizer.Normalize(r.Text))).Trim();
            body.Append("<bookmark mark=\"").Append(segment.Id.ToString(CultureInfo.InvariantCulture)).Append("\"/>")
                .Append(SsmlBuilder.Escape(text)).Append(' ');
        }

        string inner = body.ToString().TrimEnd();
        string ssml =
            $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{SsmlBuilder.Escape(language)}\">" +
            $"<voice name=\"{SsmlBuilder.Escape(voiceName)}\">{inner}</voice></speak>";
        return new SsmlDocument(ssml, inner.Length);
    }

    /// <summary>El SSML con Shoken de cada bloque, como lo arma la app para la voz de la grabación.</summary>
    public static IReadOnlyList<SsmlDocument> RecordedSsml(DemoPlan plan)
    {
        var builder = new SsmlBuilder(plan.Normalizer);
        return plan.Chunks.Select(c => builder.Build(c, RecordingVoice)).ToList();
    }

    /// <summary>Huella de un SSML (SHA-256 en hexadecimal).</summary>
    public static string Fingerprint(string ssml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ssml))).ToLowerInvariant();

    /// <summary>Cuánto del SSML facturable (todo menos &lt;speak&gt; y &lt;voice&gt;) es texto y cuánto etiquetas.</summary>
    public static SsmlCost MeasureCost(IEnumerable<SsmlDocument> documents)
    {
        int text = 0, bookmarks = 0, pauses = 0, prosody = 0;
        foreach (SsmlDocument document in documents)
        {
            int b = Length(BookmarkTag, document.Ssml);
            int p = Length(BreakTag, document.Ssml);
            int r = Length(ProsodyTag, document.Ssml);
            bookmarks += b;
            pauses += p;
            prosody += r;
            text += document.BillableCharacters - b - p - r;
        }

        return new SsmlCost(text, bookmarks, pauses, prosody);

        static int Length(Regex tag, string ssml) => tag.Matches(ssml).Sum(m => m.Length);
    }

    /// <summary>
    /// Inicio estimado de cada segmento dentro de un audio que los lee todos de corrido sin marcas (la versión plana
    /// con la voz del usuario): proporcional a la cantidad de caracteres.
    /// </summary>
    public static IReadOnlyList<TimeSpan> EstimateStarts(SpeechDocument document, TimeSpan duration)
    {
        var weights = document.Segments.Select(s => s.PlainText.Trim().Length + 1).ToList();
        double total = Math.Max(1, weights.Sum());
        var starts = new List<TimeSpan>(weights.Count);
        double before = 0;
        foreach (int weight in weights)
        {
            starts.Add(TimeSpan.FromTicks((long)(duration.Ticks * (before / total))));
            before += weight;
        }

        return starts;
    }

    internal static DemoRecording ParseRecording(string json) =>
        JsonSerializer.Deserialize<DemoRecording>(json, RecordingJson)
        ?? throw new InvalidOperationException("Los datos de la grabación de la demo están vacíos.");

    /// <summary>Formato del archivo de datos de la grabación (el mismo para leerlo y escribirlo).</summary>
    public static JsonSerializerOptions RecordingJson { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string? ReadResource(string name)
    {
        using Stream? stream = typeof(IntonationDemo).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
