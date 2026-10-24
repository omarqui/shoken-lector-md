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
/// Cadena de motores: los de la nube en orden (Azure, luego Google) y los locales al final (Kokoro, luego
/// Piper). Si un motor no está configurado, se quedó sin cuota, no hay conexión o rechaza la petición, el bloque
/// pasa al siguiente y la lectura sigue. Cada motor tiene su propia pausa antes de volver a intentarlo.
/// </summary>
public sealed class HybridTtsEngine : ITtsEngine, IDisposable
{
    private static readonly TimeSpan NetworkCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocalCooldown = TimeSpan.FromMinutes(2);

    private readonly List<Slot> _clouds;
    private readonly List<LocalSlot> _locals;
    private readonly bool _offlineOnly;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="locals">Motores sin conexión en orden de preferencia; el último es el respaldo final.</param>
    public HybridTtsEngine(IReadOnlyList<CloudEngine> clouds, IReadOnlyList<ITtsEngine> locals, bool offlineOnly, Func<DateTime>? utcNow = null)
    {
        _clouds = clouds.Select(c => new Slot(c)).ToList();
        _locals = locals.Select(l => new LocalSlot(l)).ToList();
        _offlineOnly = offlineOnly;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Status = InitialStatus();
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    public EngineStatus Status { get; private set; }

    public string Name => Status.ActiveEngine;

    private IEnumerable<Slot> ConfiguredClouds => _offlineOnly ? [] : _clouds.Where(s => s.Engine.IsConfigured);

    private List<LocalSlot> ConfiguredLocals => _locals.Where(l => l.Engine.IsConfigured).ToList();

    private bool LocalConfigured => ConfiguredLocals.Count > 0;

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

        // Igual entre los locales: el audio de Piper no se reutiliza mientras Kokoro pueda leer.
        foreach (LocalSlot slot in ConfiguredLocals)
        {
            if (slot.Engine.TryGetCached(chunk) is { } cached)
            {
                return cached;
            }

            if (_utcNow() >= slot.RestUntilUtc)
            {
                return null;
            }
        }

        return null;
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

        List<LocalSlot> locals = ConfiguredLocals;
        string? localReason = null;
        for (int i = 0; i < locals.Count; i++)
        {
            LocalSlot slot = locals[i];
            bool last = i == locals.Count - 1;
            if (!last && _utcNow() < slot.RestUntilUtc)
            {
                localReason = Join(localReason, slot.Reason); // falló hace poco: se va directo al siguiente
                continue;
            }

            try
            {
                AudioChunk audio = await slot.Engine.SynthesizeAsync(chunk, cancellationToken).ConfigureAwait(false);
                bool anyCloud = ConfiguredClouds.Any();
                string? reason = Join(anyCloud ? fallbackReason : null, localReason)
                                 ?? (!anyCloud && _offlineOnly ? "modo sin conexión" : null);
                SetStatus(new EngineStatus(slot.Engine.Name, anyCloud || localReason is not null, reason));
                return audio;
            }
            catch (TtsException ex) when (!last)
            {
                slot.RestUntilUtc = _utcNow() + LocalCooldown;
                slot.Reason = ex.Message;
                localReason = Join(localReason, ex.Message);
            }
        }

        string message = fallbackReason is null
            ? "No hay ningún motor de voz configurado. Abre Configuración y agrega tu clave de Azure o de Google, o un motor local (Kokoro o Piper)."
            : _locals.Count == 0
                ? $"No hay motor disponible ({fallbackReason}). Con el motor elegido no hay respaldo: prueba con Automático."
                : $"No hay motor disponible ({fallbackReason}) y no hay un motor local (Kokoro o Piper) configurado como respaldo.";
        // Con un motor elegido a mano que falló no hay nada que configurar: no se ofrece abrir Configuración.
        TtsFailureKind kind = _locals.Count == 0 && fallbackReason is not null ? TtsFailureKind.Other : TtsFailureKind.NotConfigured;
        throw new TtsException(kind, message);
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first}; {second}";

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
            return new EngineStatus(ConfiguredLocals[0].Engine.Name, anyCloud, anyCloud ? firstReason : _offlineOnly ? "modo sin conexión" : null);
        }

        return ConfiguredClouds.FirstOrDefault() is { } first
            ? new EngineStatus(first.Engine.Name, false, firstReason)
            : new EngineStatus("Sin motor de voz", false, "configura Azure, Google, Kokoro o Piper");
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

        foreach (LocalSlot slot in _locals)
        {
            (slot.Engine as IDisposable)?.Dispose();
        }

        _gate.Dispose();
    }

    /// <summary>Un motor local: si falla y hay otro después, descansa un rato y lee el siguiente.</summary>
    private sealed class LocalSlot(ITtsEngine engine)
    {
        public ITtsEngine Engine { get; } = engine;
        public DateTime RestUntilUtc { get; set; } = DateTime.MinValue;
        public string? Reason { get; set; }
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
