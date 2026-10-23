using System.Globalization;
using System.Text.Json;

namespace LectorMd.Core.Usage;

/// <summary>
/// Cuenta los caracteres enviados a Azure en el mes en curso (UTC) para no pasar de la capa gratuita.
/// Es una estimación local: la fuente de verdad son las métricas del recurso en el portal de Azure.
/// </summary>
public sealed class UsageTracker
{
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private UsageState _state;

    public UsageTracker(string path, int monthlyLimit, Func<DateTime>? utcNow = null)
    {
        _path = path;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Limit = Math.Max(0, monthlyLimit);
        _state = Load();
    }

    public event EventHandler? Changed;

    public int Limit { get; }

    public string CurrentMonth => _utcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public int Used
    {
        get
        {
            lock (_lock)
            {
                RollOver();
                return _state.AzureCharacters;
            }
        }
    }

    /// <summary>True si se llegó al límite local o si Azure respondió que la cuota se agotó este mes.</summary>
    public bool IsExhausted
    {
        get
        {
            lock (_lock)
            {
                RollOver();
                return _state.ServiceExhausted || _state.AzureCharacters >= Limit;
            }
        }
    }

    public bool ServiceReportedExhausted
    {
        get
        {
            lock (_lock)
            {
                RollOver();
                return _state.ServiceExhausted;
            }
        }
    }

    public bool CanSpend(int characters)
    {
        lock (_lock)
        {
            RollOver();
            return !_state.ServiceExhausted && _state.AzureCharacters + characters <= Limit;
        }
    }

    public void Add(int characters)
    {
        lock (_lock)
        {
            _state = Load(); // otra instancia (p. ej. el botón "Probar") pudo haber sumado mientras tanto
            RollOver();
            _state = _state with { AzureCharacters = _state.AzureCharacters + Math.Max(0, characters) };
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkServiceExhausted()
    {
        lock (_lock)
        {
            _state = Load();
            RollOver();
            _state = _state with { ServiceExhausted = true };
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RollOver()
    {
        string month = CurrentMonth;
        if (_state.Month != month)
        {
            _state = new UsageState(month, 0, false);
            Save();
        }
    }

    private UsageState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var state = JsonSerializer.Deserialize<UsageState>(File.ReadAllText(_path));
                if (state is not null)
                {
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Archivo dañado: se empieza de cero (mejor subestimar que bloquear la lectura).
        }

        return new UsageState(CurrentMonth, 0, false);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No es crítico: se reintentará en la siguiente escritura.
        }
    }

    private sealed record UsageState(string Month, int AzureCharacters, bool ServiceExhausted);
}
