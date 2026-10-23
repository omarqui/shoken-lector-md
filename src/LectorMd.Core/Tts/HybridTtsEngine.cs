using LectorMd.Core.Planning;
using LectorMd.Core.Usage;

namespace LectorMd.Core.Tts;

/// <param name="ActiveEngine">Nombre del motor que produjo el último audio.</param>
/// <param name="UsingFallback">True si un motor de más prioridad está configurado pero no se pudo usar.</param>
/// <param name="Reason">Por qué se usa el respaldo (o null).</param>
public sealed record EngineStatus(string ActiveEngine, bool UsingFallback, string? Reason);

/// <summary>Un motor en la nube de la cadena, con su contador de cuota y el nombre corto para los mensajes.</summary>
public sealed record CloudEngine(ITtsEngine Engine, UsageTracker Usage, string Label);

/// <summary>
/// Cadena de motores: los de la nube en orden (Azure, luego Google) y Piper al final. Si un motor no está
/// configurado, se quedó sin cuota, no hay conexión o rechaza la petición, el bloque pasa al siguiente y la
/// lectura sigue. Cada motor en la nube tiene su propia pausa antes de volver a intentarlo.
/// </summary>
public sealed class HybridTtsEngine : ITtsEngine, IDisposable
{
    private static readonly TimeSpan NetworkCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorCooldown = TimeSpan.FromSeconds(30);

    private readonly List<Slot> _clouds;
    private readonly ITtsEngine? _local;
    private readonly bool _offlineOnly;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HybridTtsEngine(IReadOnlyList<CloudEngine> clouds, ITtsEngine? local, bool offlineOnly, Func<DateTime>? utcNow = null)
    {
        _clouds = clouds.Select(c => new Slot(c)).ToList();
        _local = local;
        _offlineOnly = offlineOnly;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Status = InitialStatus();
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    public EngineStatus Status { get; private set; }

    public string Name => Status.ActiveEngine;

    private IEnumerable<Slot> ConfiguredClouds => _offlineOnly ? [] : _clouds.Where(s => s.Engine.IsConfigured);

    private bool LocalConfigured => _local is { IsConfigured: true };

    public bool IsConfigured => ConfiguredClouds.Any() || LocalConfigured;

    /// <summary>
    /// Audio ya generado, sin gastar cuota. El de un motor de respaldo solo se reutiliza mientras los de más
    /// prioridad no estén disponibles, para no quedarse con la voz de respaldo cuando el principal vuelve.
    /// </summary>
    public AudioChunk? TryGetCached(SpeechChunk chunk)
    {
        foreach (Slot slot in ConfiguredClouds)
        {
            if (slot.Engine.TryGetCached(chunk) is { } cached)
            {
                return cached;
            }

            if (UnavailableReason(slot) is null)
            {
                return null;
            }
        }

        return LocalConfigured ? _local!.TryGetCached(chunk) : null;
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
        // El motor principal que no se pudo usar: su motivo es el que ve el usuario.
        string? fallbackReason = null;

        foreach (Slot slot in ConfiguredClouds)
        {
            // El audio ya pagado se reutiliza aunque hoy no haya cuota o conexión.
            if (slot.Engine.TryGetCached(chunk) is { } cached)
            {
                SetStatus(new EngineStatus(slot.Engine.Name, fallbackReason is not null, fallbackReason));
                return cached;
            }

            string? reason = UnavailableReason(slot);
            if (reason is null)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        AudioChunk audio = await slot.Engine.SynthesizeAsync(chunk, cancellationToken).ConfigureAwait(false);
                        SetStatus(new EngineStatus(slot.Engine.Name, fallbackReason is not null, fallbackReason));
                        return audio;
                    }
                    catch (TtsException ex) when (ex.Kind == TtsFailureKind.RateLimited && attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TtsException ex)
                    {
                        reason = RegisterFailure(slot, ex);
                        break;
                    }
                }
            }

            // Se juntan los motivos de todos los motores que no se pudieron usar (no solo el del primero).
            fallbackReason = fallbackReason is null ? reason : $"{fallbackReason}; {slot.Label}: {reason}";
        }

        if (LocalConfigured)
        {
            AudioChunk audio = await _local!.SynthesizeAsync(chunk, cancellationToken).ConfigureAwait(false);
            bool anyCloud = ConfiguredClouds.Any();
            string? reason = anyCloud ? fallbackReason : _offlineOnly ? "modo sin conexión" : null;
            SetStatus(new EngineStatus(_local.Name, anyCloud, reason));
            return audio;
        }

        string message = fallbackReason is null
            ? "No hay ningún motor de voz configurado. Abre Configuración y agrega tu clave de Azure o de Google, o Piper."
            : _local is null
                ? $"No hay motor disponible ({fallbackReason}). Con el motor elegido no hay respaldo: prueba con Automático."
                : $"No hay motor disponible ({fallbackReason}) y Piper no está configurado como respaldo.";
        // Con un motor elegido a mano que falló no hay nada que configurar: no se ofrece abrir Configuración.
        TtsFailureKind kind = _local is null && fallbackReason is not null ? TtsFailureKind.Other : TtsFailureKind.NotConfigured;
        throw new TtsException(kind, message);
    }

    private string? UnavailableReason(Slot slot)
    {
        if (slot.DisabledReason is not null)
        {
            return slot.DisabledReason;
        }

        if (slot.Usage.ServiceReportedExhausted)
        {
            return $"{slot.Label} indicó que la cuota del mes se agotó";
        }

        if (slot.Usage.IsExhausted)
        {
            return $"se alcanzó el límite mensual de {slot.Usage.Limit:N0} caracteres de {slot.Label}";
        }

        return _utcNow() < slot.RetryAfterUtc ? slot.CooldownReason : null;
    }

    private string RegisterFailure(Slot slot, TtsException ex)
    {
        switch (ex.Kind)
        {
            case TtsFailureKind.Authentication:
            case TtsFailureKind.NotConfigured:
                slot.DisabledReason = ex.Message;
                break;
            case TtsFailureKind.QuotaExceeded:
                slot.Usage.MarkServiceExhausted();
                break;
            case TtsFailureKind.LocalLimitReached:
                break;
            case TtsFailureKind.RateLimited:
                Cooldown(slot, RateLimitCooldown, ex.Message);
                break;
            case TtsFailureKind.Network:
                Cooldown(slot, NetworkCooldown, ex.Message);
                break;
            default:
                Cooldown(slot, ErrorCooldown, ex.Message);
                break;
        }

        return ex.Message;
    }

    private void Cooldown(Slot slot, TimeSpan duration, string reason)
    {
        slot.RetryAfterUtc = _utcNow() + duration;
        slot.CooldownReason = reason;
    }

    private EngineStatus InitialStatus()
    {
        string? firstReason = null;
        foreach (Slot slot in ConfiguredClouds)
        {
            string? reason = UnavailableReason(slot);
            if (reason is null)
            {
                return new EngineStatus(slot.Engine.Name, firstReason is not null, firstReason);
            }

            firstReason ??= reason;
        }

        if (LocalConfigured)
        {
            bool anyCloud = ConfiguredClouds.Any();
            return new EngineStatus(_local!.Name, anyCloud, anyCloud ? firstReason : _offlineOnly ? "modo sin conexión" : null);
        }

        return ConfiguredClouds.FirstOrDefault() is { } first
            ? new EngineStatus(first.Engine.Name, false, firstReason)
            : new EngineStatus("Sin motor de voz", false, "configura Azure, Google o Piper");
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
        foreach (Slot slot in _clouds)
        {
            (slot.Engine as IDisposable)?.Dispose();
        }

        (_local as IDisposable)?.Dispose();
        _gate.Dispose();
    }

    /// <summary>Estado de un motor en la nube: deshabilitado (clave inválida) o en pausa tras una falla.</summary>
    private sealed class Slot(CloudEngine cloud)
    {
        public ITtsEngine Engine { get; } = cloud.Engine;
        public UsageTracker Usage { get; } = cloud.Usage;
        public string Label { get; } = cloud.Label;
        public string? DisabledReason { get; set; }
        public DateTime RetryAfterUtc { get; set; } = DateTime.MinValue;
        public string? CooldownReason { get; set; }
    }
}
