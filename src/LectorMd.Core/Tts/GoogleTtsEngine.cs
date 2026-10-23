using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LectorMd.Core.Caching;
using LectorMd.Core.Planning;
using LectorMd.Core.Ssml;
using LectorMd.Core.Usage;

namespace LectorMd.Core.Tts;

public sealed class GoogleTtsOptions
{
    /// <summary>Clave de API de Google Cloud, restringida a Text-to-Speech. La app la guarda cifrada.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Voz Neural2: es-US-Neural2-A (mujer), B y C (hombres). Soportan SSML y marcas de tiempo.</summary>
    public string Voice { get; init; } = "es-US-Neural2-A";

    /// <summary>Velocidad de síntesis en % (-30..+30), la misma de Configuración.</summary>
    public int RatePercent { get; init; }
}

/// <summary>
/// Google Cloud Text-to-Speech por REST, como respaldo de Azure. Pide las marcas de tiempo de cada segmento
/// (<c>SSML_MARK</c>) para que el resaltado y la línea de tiempo funcionen igual que con Azure.
/// </summary>
public sealed class GoogleTtsEngine(GoogleTtsOptions options, SsmlBuilder ssml, AudioCache cache, UsageTracker usage, HttpClient http)
    : ITtsEngine
{
    // v1beta1: la versión que devuelve las marcas de tiempo.
    private const string Endpoint = "https://texttospeech.googleapis.com/v1beta1/text:synthesize";
    private const string FormatId = "mp3";

    public string Name => $"Google · {options.Voice}";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    /// <summary>"es-US-Neural2-A" → "es-US".</summary>
    private string LanguageCode => string.Join('-', options.Voice.Split('-').Take(2));

    public AudioChunk? TryGetCached(SpeechChunk chunk) => cache.TryGet(Prepare(chunk).Key, chunk, Name);

    public async Task<AudioChunk> SynthesizeAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        (string googleSsml, string key) = Prepare(chunk);
        if (cache.TryGet(key, chunk, Name) is { } cached)
        {
            return cached;
        }

        // Google no corta al pasar el plan gratuito: cobra. El límite local es la única protección.
        int billable = googleSsml.Length;
        if (!usage.CanSpend(billable))
        {
            throw new TtsException(TtsFailureKind.LocalLimitReached,
                $"se alcanzó el límite mensual de Google ({usage.Limit:N0} caracteres)");
        }

        string body = JsonSerializer.Serialize(new
        {
            input = new { ssml = googleSsml },
            voice = new { languageCode = LanguageCode, name = options.Voice },
            audioConfig = new { audioEncoding = "MP3" },
            enableTimePointing = new[] { "SSML_MARK" },
        });

        // Una vez enviado, el pedido no se cancela (igual que con Azure): si el usuario se movió, Google lo cobra
        // igual, así que se cuenta y el audio queda en la caché para cuando vuelva. El límite es el Timeout.
        cancellationToken.ThrowIfCancellationRequested();
        string responseBody;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.PostAsync(
                $"{Endpoint}?key={Uri.EscapeDataString(options.ApiKey.Trim())}", content, CancellationToken.None).ConfigureAwait(false);
            responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response.StatusCode, responseBody);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new TtsException(TtsFailureKind.Network, $"sin conexión con Google ({ex.Message})", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new TtsException(TtsFailureKind.Network, "Google tardó demasiado en responder", ex);
        }

        // Google ya respondió (y cobró): se cuenta aunque la respuesta venga mal formada.
        usage.Add(billable);
        (byte[] audio, List<(int Index, TimeSpan Offset)> marks) = ParseResponse(responseBody);
        return cache.Store(key, audio, ".mp3", marks, chunk, Name);
    }

    internal static TtsException MapError(HttpStatusCode status, string body)
    {
        string detail = ErrorMessage(body);
        string info = detail.Length > 0 ? $" ({detail})" : "";

        // Una clave inválida o vencida llega como 400 (no 403): si no se reconoce, se reintentaría en cada bloque.
        if (body.Contains("API_KEY_INVALID", StringComparison.Ordinal) || body.Contains("API_KEY_EXPIRED", StringComparison.Ordinal))
        {
            return new TtsException(TtsFailureKind.Authentication, "Google rechazó la clave de API" + info);
        }

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new TtsException(TtsFailureKind.Authentication, "Google rechazó la clave o la API de Text-to-Speech no está habilitada" + info),
            // Al pasar el plan gratuito Google cobra, no responde 429: un 429 es el límite por minuto.
            HttpStatusCode.TooManyRequests =>
                new TtsException(TtsFailureKind.RateLimited, "demasiadas peticiones seguidas a Google" + info),
            HttpStatusCode.BadRequest =>
                new TtsException(TtsFailureKind.Other, "Google rechazó la petición" + info),
            >= HttpStatusCode.InternalServerError =>
                new TtsException(TtsFailureKind.Network, "Google no está respondiendo" + info),
            _ => new TtsException(TtsFailureKind.Other, $"error de Google ({(int)status}){info}"),
        };
    }

    private (string Ssml, string Key) Prepare(SpeechChunk chunk)
    {
        SsmlDocument document = ssml.Build(chunk, options.Voice, options.RatePercent, LanguageCode);
        string googleSsml = GoogleSsml.FromAzure(document.Ssml);
        return (googleSsml, AudioCache.ComputeKey("google", FormatId, options.Voice, googleSsml));
    }

    private static (byte[] Audio, List<(int Index, TimeSpan Offset)> Marks) ParseResponse(string body)
    {
        try
        {
            return ParseResponseCore(body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            // Una respuesta inesperada no debe cortar la lectura: el motor híbrido pasa al siguiente.
            throw new TtsException(TtsFailureKind.Other, "Google respondió algo que no es audio", ex);
        }
    }

    private static (byte[] Audio, List<(int Index, TimeSpan Offset)> Marks) ParseResponseCore(string body)
    {
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        byte[] audio = Convert.FromBase64String(root.GetProperty("audioContent").GetString() ?? "");

        var marks = new List<(int, TimeSpan)>();
        if (root.TryGetProperty("timepoints", out JsonElement timepoints) && timepoints.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement point in timepoints.EnumerateArray())
            {
                if (point.TryGetProperty("markName", out JsonElement name)
                    && int.TryParse(name.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    && point.TryGetProperty("timeSeconds", out JsonElement seconds) && seconds.ValueKind == JsonValueKind.Number)
                {
                    marks.Add((index, TimeSpan.FromSeconds(seconds.GetDouble())));
                }
            }
        }

        return (audio, marks);
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 200 ? body[..200] : body;
    }
}
