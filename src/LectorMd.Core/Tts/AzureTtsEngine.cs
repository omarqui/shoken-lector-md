using LectorMd.Core.Caching;
using LectorMd.Core.Planning;
using LectorMd.Core.Ssml;
using LectorMd.Core.Usage;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace LectorMd.Core.Tts;

public sealed class AzureTtsOptions
{
    public required string Key { get; init; }
    public required string Region { get; init; }
    public string Voice { get; init; } = "es-MX-DaliaNeural";
    public int RatePercent { get; init; }
}

/// <summary>
/// Motor principal: voces neuronales de Azure Speech. Sintetiza un bloque por petición (la capa F0
/// permite 20 peticiones por minuto) y usa los eventos de bookmark para saber cuándo empieza cada segmento.
/// </summary>
public sealed class AzureTtsEngine : ITtsEngine, IDisposable
{
    private const string FormatId = "mp3-24khz-96kbps";

    private readonly AzureTtsOptions _options;
    private readonly SsmlBuilder _ssml;
    private readonly AudioCache _cache;
    private readonly UsageTracker _usage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _marksLock = new();
    private SpeechSynthesizer? _synthesizer;
    private List<(int Index, TimeSpan Offset)>? _marks;
    private bool _disposed;

    public AzureTtsEngine(AzureTtsOptions options, SsmlBuilder ssml, AudioCache cache, UsageTracker usage)
    {
        _options = options;
        _ssml = ssml;
        _cache = cache;
        _usage = usage;
    }

    public string Name => $"Azure · {_options.Voice}";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Key) && !string.IsNullOrWhiteSpace(_options.Region);

    public AudioChunk? TryGetCached(SpeechChunk chunk)
    {
        (_, string key) = Prepare(chunk);
        return _cache.TryGet(key, chunk, Name);
    }

    public async Task<AudioChunk> SynthesizeAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConfigured)
        {
            throw new TtsException(TtsFailureKind.NotConfigured, "Azure no está configurado (falta la clave o la región).");
        }

        (SsmlDocument document, string key) = Prepare(chunk);
        if (_cache.TryGet(key, chunk, Name) is { } cached)
        {
            return cached;
        }

        if (!_usage.CanSpend(document.BillableCharacters))
        {
            throw new TtsException(TtsFailureKind.LocalLimitReached,
                $"se alcanzó el límite mensual configurado ({_usage.Limit:N0} caracteres)");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Una vez enviada, la petición no se cancela: si el usuario saltó a otra parte,
            // el audio queda en caché para cuando vuelva.
            cancellationToken.ThrowIfCancellationRequested();

            SpeechSynthesizer synthesizer = _synthesizer ??= CreateSynthesizer();
            var marks = new List<(int, TimeSpan)>();
            lock (_marksLock)
            {
                _marks = marks;
            }

            SpeechSynthesisResult result;
            try
            {
                result = await synthesizer.SpeakSsmlAsync(document.Ssml).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ResetSynthesizer();
                throw new TtsException(TtsFailureKind.Network, $"no se pudo conectar con Azure: {ex.Message}", ex);
            }
            finally
            {
                lock (_marksLock)
                {
                    _marks = null;
                }
            }

            using (result)
            {
                switch (result.Reason)
                {
                    case ResultReason.SynthesizingAudioCompleted:
                        _usage.Add(document.BillableCharacters);
                        List<(int, TimeSpan)> snapshot;
                        lock (_marksLock)
                        {
                            snapshot = [.. marks];
                        }

                        return _cache.Store(key, result.AudioData, ".mp3", snapshot, chunk, Name);

                    case ResultReason.Canceled:
                        var details = SpeechSynthesisCancellationDetails.FromResult(result);
                        ResetSynthesizer();
                        throw MapError(details.ErrorCode, details.ErrorDetails);

                    default:
                        throw new TtsException(TtsFailureKind.Other, $"respuesta inesperada de Azure ({result.Reason})");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private (SsmlDocument Document, string Key) Prepare(SpeechChunk chunk)
    {
        SsmlDocument document = _ssml.Build(chunk, _options.Voice, _options.RatePercent);
        return (document, AudioCache.ComputeKey("azure", FormatId, document.Ssml));
    }

    private SpeechSynthesizer CreateSynthesizer()
    {
        var config = SpeechConfig.FromSubscription(_options.Key.Trim(), _options.Region.Trim());
        config.SpeechSynthesisVoiceName = _options.Voice;
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz96KBitRateMonoMp3);

        // AudioConfig nulo: el audio se recibe en memoria (result.AudioData) en lugar de ir a la bocina.
        var synthesizer = new SpeechSynthesizer(config, null as AudioConfig);
        synthesizer.BookmarkReached += (_, e) =>
        {
            if (int.TryParse(e.Text, out int index))
            {
                lock (_marksLock)
                {
                    _marks?.Add((index, TimeSpan.FromTicks((long)e.AudioOffset)));
                }
            }
        };
        return synthesizer;
    }

    private void ResetSynthesizer()
    {
        _synthesizer?.Dispose();
        _synthesizer = null;
    }

    internal static TtsException MapError(CancellationErrorCode code, string? details)
    {
        string info = string.IsNullOrWhiteSpace(details) ? "" : $" ({details.Trim()})";
        bool mentionsQuota = details?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true;

        return code switch
        {
            CancellationErrorCode.AuthenticationFailure =>
                new TtsException(TtsFailureKind.Authentication, "Azure rechazó la clave o la región" + info),
            CancellationErrorCode.Forbidden when mentionsQuota =>
                new TtsException(TtsFailureKind.QuotaExceeded, "Azure indica que la cuota gratuita del mes se agotó" + info),
            CancellationErrorCode.Forbidden =>
                new TtsException(TtsFailureKind.Authentication, "Azure denegó el acceso" + info),
            CancellationErrorCode.TooManyRequests when mentionsQuota =>
                new TtsException(TtsFailureKind.QuotaExceeded, "Azure indica que la cuota gratuita del mes se agotó" + info),
            CancellationErrorCode.TooManyRequests =>
                new TtsException(TtsFailureKind.RateLimited, "demasiadas peticiones seguidas a Azure" + info),
            CancellationErrorCode.ConnectionFailure or CancellationErrorCode.ServiceTimeout or CancellationErrorCode.ServiceUnavailable =>
                new TtsException(TtsFailureKind.Network, "sin conexión con Azure" + info),
            CancellationErrorCode.BadRequest =>
                new TtsException(TtsFailureKind.Other, "Azure rechazó la petición" + info),
            _ => new TtsException(TtsFailureKind.Other, "error de Azure" + info),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetSynthesizer();
        _gate.Dispose();
    }
}
