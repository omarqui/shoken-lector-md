using System.Text.Json.Serialization;
using LectorMd.Core.Parsing;

namespace LectorMd.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Clave de Azure Speech. La app la guarda cifrada (DPAPI), nunca en texto plano.</summary>
    [JsonIgnore]
    public string AzureKey { get; set; } = "";

    /// <summary>Región del recurso de Azure Speech, p. ej. "eastus" o "southcentralus".</summary>
    public string AzureRegion { get; set; } = "eastus";

    public string AzureVoice { get; set; } = "es-MX-DaliaNeural";

    public string PiperExePath { get; set; } = "";

    /// <summary>Ruta al modelo .onnx (con su .onnx.json al lado), p. ej. es_MX-claude-high.onnx.</summary>
    public string PiperModelPath { get; set; } = "";

    /// <summary>Velocidad de síntesis en % (-30..+30). Cambiarla invalida la caché de audio.</summary>
    public int RatePercent { get; set; }

    /// <summary>Velocidad de reproducción (0.75..1.75). No gasta cuota ni invalida la caché.</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    /// <summary>Límite mensual de caracteres para Azure. La capa F0 incluye 500 000; se deja margen.</summary>
    public int MonthlyCharacterLimit { get; set; } = 480_000;

    /// <summary>Usar solo Piper (sin conexión) aunque Azure esté configurado.</summary>
    public bool OfflineOnly { get; set; }

    public CodeBlockMode CodeBlocks { get; set; } = CodeBlockMode.Announce;

    public TableMode Tables { get; set; } = TableMode.ReadRows;

    public bool ReadImageAltText { get; set; }

    /// <summary>Último archivo de la versión sin pestañas. Solo se lee para migrar a <see cref="OpenTabs"/>.</summary>
    public string? LastFile { get; set; }

    public List<OpenTab> OpenTabs { get; set; } = [];

    /// <summary>Índice en <see cref="OpenTabs"/> de la pestaña activa.</summary>
    public int ActiveTab { get; set; }

    /// <summary>Panel del índice de títulos visible.</summary>
    public bool ShowOutline { get; set; } = true;

    /// <summary>Id de ARM del recurso de Speech, para consultar su consumo real en Azure Monitor.</summary>
    public string? AzureResourceId { get; set; }

    /// <summary>Directorio (tenant) de Entra ID al que pertenece <see cref="AzureResourceId"/>.</summary>
    public string? AzureTenantId { get; set; }

    [JsonIgnore]
    public bool HasAzure => !string.IsNullOrWhiteSpace(AzureKey) && !string.IsNullOrWhiteSpace(AzureRegion);

    [JsonIgnore]
    public bool HasPiper => File.Exists(PiperExePath) && File.Exists(PiperModelPath);

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.OpenTabs = OpenTabs.Select(t => t.Clone()).ToList();
        return clone;
    }
}

/// <summary>Rutas de datos de la app (por defecto en %LOCALAPPDATA%\LectorMd).</summary>
public sealed class AppPaths(string root)
{
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LectorMd"));

    public string Root { get; } = root;
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string UsageFile => Path.Combine(Root, "uso-azure.json");
    public string PronunciationFile => Path.Combine(Root, "pronunciacion.json");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string ViewDirectory => Path.Combine(Root, "view");
    public string WebViewDataDirectory => Path.Combine(Root, "webview");
    public string TempDirectory => Path.Combine(Root, "temp");
    public string ErrorLogFile => Path.Combine(Root, "errores.log");

    /// <summary>Texto Markdown pegado en pestañas; cada archivo se borra al cerrar su pestaña.</summary>
    public string PastedDirectory => Path.Combine(Root, "pegados");

    /// <summary>Cuenta de Azure con la que se consulta el consumo (sin secretos: el token va en la caché cifrada).</summary>
    public string AzureAccountFile => Path.Combine(Root, "cuenta-azure.json");

    public void EnsureCreated()
    {
        foreach (string dir in new[] { Root, CacheDirectory, ViewDirectory, WebViewDataDirectory, TempDirectory, PastedDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
