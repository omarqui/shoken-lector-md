using LectorMd.Core.Planning;
using LectorMd.Core.Usage;

namespace LectorMd.Core.Tts;

/// <param name="ActiveEngine">Nombre del motor que produjo el último audio.</param>
/// <param name="UsingFallback">True si Azure está configurado pero se tuvo que usar Piper.</param>
/// <param name="Reason">Por qué se usa el respaldo (o null).</param>
public sealed record EngineStatus(string ActiveEngine, bool UsingFallback, string? Reason);

/// <summary>
/// Azure como motor principal y Piper como respaldo: si Azure no está configurado, se agotó la cuota,
/// no hay conexión o rechaza la petición, el bloque se sintetiza con Piper y la lectura sigue.
/// </summary>
public sealed class HybridTtsEngine : ITtsEngine, IDisposable
{
    private static readonly TimeSpan NetworkCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorCooldown = TimeSpan.FromSeconds(30);

    private readonly AzureTtsEngine? _azure;
    private readonly PiperTtsEngine? _piper;
    private readonly UsageTracker _usage;
    private readonly bool _offlineOnly;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _azureDisabledReason;
    private DateTime _azureRetryAfterUtc = DateTime.MinValue;
    private string? _cooldownReason;

    public HybridTtsEngine(AzureTtsEngine? azure, PiperTtsEngine? piper, UsageTracker usage, bool offlineOnly,
        Func<DateTime>? utcNow = null)
    {
        _azure = azure;
        _piper = piper;
        _usage = usage;
        _offlineOnly = offlineOnly;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Status = InitialStatus();
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    public EngineStatus Status { get; private set; }

    public string Name => Status.ActiveEngine;

    private bool AzureConfigured => !_offlineOnly && _azure is { IsConfigured: true };

    private bool PiperConfigured => _piper is { IsConfigured: true };

    public bool IsConfigured => AzureConfigured || PiperConfigured;

    /// <summary>
    /// Audio de Azure en caché si existe. El de Piper solo se reutiliza mientras Azure no esté disponible,
    /// para no quedarse con la voz de respaldo cuando Azure vuelve.
    /// </summary>
    public AudioChunk? TryGetCached(SpeechChunk chunk)
    {
        if (AzureConfigured)
        {
            if (_azure!.TryGetCached(chunk) is { } azure)
            {
                return azure;
            }

            if (AzureUnavailableReason() is null)
            {
                return null;
            }
        }

        return PiperConfigured ? _piper!.TryGetCached(chunk) : null;
    }

    public async Task<AudioChunk> SynthesizeAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SynthesizeCoreAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AudioChunk> SynthesizeCoreAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        string? fallbackReason = null;

        if (AzureConfigured)
        {
            // El audio ya pagado se reutiliza aunque hoy no haya cuota o conexión.
            if (_azure!.TryGetCached(chunk) is { } cached)
            {
                SetStatus(new EngineStatus(_azure.Name, false, null));
                return cached;
            }

            fallbackReason = AzureUnavailableReason();
            if (fallbackReason is null)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        AudioChunk audio = await _azure.SynthesizeAsync(chunk, cancellationToken).ConfigureAwait(false);
                        SetStatus(new EngineStatus(_azure.Name, false, null));
                        return audio;
                    }
                    catch (TtsException ex) when (ex.Kind == TtsFailureKind.RateLimited && attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TtsException ex)
                    {
                        fallbackReason = RegisterAzureFailure(ex);
                        break;
                    }
                }
            }
        }

        if (PiperConfigured)
        {
            AudioChunk audio = await _piper!.SynthesizeAsync(chunk, cancellationToken).ConfigureAwait(false);
            string? reason = AzureConfigured ? fallbackReason : _offlineOnly ? "modo sin conexión" : null;
            SetStatus(new EngineStatus(_piper.Name, AzureConfigured, reason));
            return audio;
        }

        string message = fallbackReason is null
            ? "No hay ningún motor de voz configurado. Abre Configuración y agrega tu clave de Azure o Piper."
            : $"Azure no está disponible ({fallbackReason}) y Piper no está configurado como respaldo.";
        throw new TtsException(TtsFailureKind.NotConfigured, message);
    }

    private string? AzureUnavailableReason()
    {
        if (_azureDisabledReason is not null)
        {
            return _azureDisabledReason;
        }

        if (_usage.ServiceReportedExhausted)
        {
            return "Azure indicó que la cuota del mes se agotó";
        }

        if (_usage.IsExhausted)
        {
            return $"se alcanzó el límite mensual de {_usage.Limit:N0} caracteres";
        }

        return _utcNow() < _azureRetryAfterUtc ? _cooldownReason : null;
    }

    private string RegisterAzureFailure(TtsException ex)
    {
        switch (ex.Kind)
        {
            case TtsFailureKind.Authentication:
            case TtsFailureKind.NotConfigured:
                _azureDisabledReason = ex.Message;
                break;
            case TtsFailureKind.QuotaExceeded:
                _usage.MarkServiceExhausted();
                break;
            case TtsFailureKind.LocalLimitReached:
                break;
            case TtsFailureKind.RateLimited:
                Cooldown(RateLimitCooldown, ex.Message);
                break;
            case TtsFailureKind.Network:
                Cooldown(NetworkCooldown, ex.Message);
                break;
            default:
                Cooldown(ErrorCooldown, ex.Message);
                break;
        }

        return ex.Message;
    }

    private void Cooldown(TimeSpan duration, string reason)
    {
        _azureRetryAfterUtc = _utcNow() + duration;
        _cooldownReason = reason;
    }

    private EngineStatus InitialStatus()
    {
        if (AzureConfigured)
        {
            string? reason = AzureUnavailableReason();
            if (reason is not null && PiperConfigured)
            {
                return new EngineStatus(_piper!.Name, true, reason);
            }

            return new EngineStatus(_azure!.Name, false, reason);
        }

        if (PiperConfigured)
        {
            return new EngineStatus(_piper!.Name, false, _offlineOnly ? "modo sin conexión" : null);
        }

        return new EngineStatus("Sin motor de voz", false, "configura Azure o Piper");
    }

    private void SetStatus(EngineStatus status)
    {
        if (status == Status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        _azure?.Dispose();
        _piper?.Dispose();
        _gate.Dispose();
    }
}
