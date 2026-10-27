using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Shoken.Core.Parsing;

namespace Shoken.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Clave de Azure Speech. La app la guarda cifrada (DPAPI), nunca en texto plano.</summary>
    [JsonIgnore]
    public string AzureKey { get; set; } = "";

    /// <summary>Región del recurso de Azure Speech, p. ej. "eastus" o "southcentralus".</summary>
    public string AzureRegion { get; set; } = "eastus";

    public string AzureVoice { get; set; } = "es-MX-DaliaNeural";

    /// <summary>Voz de Piper que se descarga desde Configuración (es_MX-claude-high o es_MX-ald-medium).</summary>
    public string PiperVoice { get; set; } = "es_MX-claude-high";

    /// <summary>
    /// piper.exe elegido a mano, de antes de que Piper se descargara desde Configuración. Solo se usa si no se
    /// descargó (ver <see cref="EngineServices.ResolvePiper"/>); la interfaz ya no lo muestra.
    /// </summary>
    public string PiperExePath { get; set; } = "";

    /// <summary>Modelo .onnx elegido a mano (con su .onnx.json al lado), igual que <see cref="PiperExePath"/>.</summary>
    public string PiperModelPath { get; set; } = "";

    /// <summary>Voz de Kokoro en español (ef_dora, em_alex o em_santa). Su modelo vive en <see cref="AppPaths.KokoroModelFile"/>.</summary>
    public string KokoroVoice { get; set; } = "ef_dora";

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

    /// <summary>Marcar, dentro del párrafo que se lee, la palabra que suena.</summary>
    public bool HighlightWord { get; set; } = true;

    /// <summary>Último archivo de la versión sin pestañas. Solo se lee para migrar a <see cref="OpenTabs"/>.</summary>
    public string? LastFile { get; set; }

    public List<OpenTab> OpenTabs { get; set; } = [];

    /// <summary>Índice en <see cref="OpenTabs"/> de la pestaña activa.</summary>
    public int ActiveTab { get; set; }

    /// <summary>Panel del índice de títulos visible.</summary>
    public bool ShowOutline { get; set; } = true;

    /// <summary>Ya se avisó (una vez) que cerrar la ventana deja la app en la bandeja.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Clave de API de Google Cloud (restringida a Text-to-Speech). Se guarda cifrada (DPAPI).</summary>
    [JsonIgnore]
    public string GoogleApiKey { get; set; } = "";

    /// <summary>Voz Neural2: es-US-Neural2-A (mujer), B o C (hombres).</summary>
    public string GoogleVoice { get; set; } = "es-US-Neural2-A";

    /// <summary>Límite mensual para Google: su plan gratuito de Neural2 es 1 000 000; se deja margen.</summary>
    public int GoogleMonthlyCharacterLimit { get; set; } = 950_000;

    /// <summary>Motor elegido en el panel del estado: automático (Azure → Google → Piper) o uno solo, para probar.</summary>
    public EngineChoice Engine { get; set; } = EngineChoice.Auto;

    [JsonIgnore]
    public bool HasGoogle => !string.IsNullOrWhiteSpace(GoogleApiKey);

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Tema de color (cada uno con versión clara y oscura); <see cref="Theme"/> elige cuál se ve.</summary>
    public ThemePalette Palette { get; set; } = ThemePalette.Paper;

    /// <summary>Columna de apuntes a la derecha del documento (cuando el documento tiene apuntes).</summary>
    public bool ShowNotes { get; set; } = true;

    /// <summary>Instrucción para la IA al copiar los apuntes; null = la que trae la app (NoteExporter.DefaultInstruction).</summary>
    public string? NotesInstruction { get; set; }

    /// <summary>Tamaño de los textos de la interfaz (no del documento, que se agranda con Ctrl+rueda).</summary>
    public TextSize TextSize { get; set; } = TextSize.Large;

    /// <summary>Id de ARM del recurso de Speech, para consultar su consumo real en Azure Monitor.</summary>
    public string? AzureResourceId { get; set; }

    /// <summary>Directorio (tenant) de Entra ID al que pertenece <see cref="AzureResourceId"/>.</summary>
    public string? AzureTenantId { get; set; }

    [JsonIgnore]
    public bool HasAzure => !string.IsNullOrWhiteSpace(AzureKey) && !string.IsNullOrWhiteSpace(AzureRegion);

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.OpenTabs = OpenTabs.Select(t => t.Clone()).ToList();
        return clone;
    }
}

/// <summary>Automático = la cadena completa con respaldos; los demás usan solo ese motor (sin respaldo).</summary>
public enum EngineChoice
{
    Auto,
    Azure,
    Google,
    Kokoro,
    Piper,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Rutas de datos de la app: %LOCALAPPDATA%\Shoken o, si corre empaquetada (Store), el LocalState del paquete.
/// </summary>
public sealed class AppPaths(string root)
{
    /// <summary>Familia del paquete MSIX de la app, o null si corre sin empaquetar (dotnet run, publicar\).</summary>
    public static string? PackageFamilyName { get; } = CurrentPackageFamilyName();

    public static AppPaths Default { get; } = new(DefaultRoot(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PackageFamilyName));

    /// <summary>
    /// Empaquetada, Windows redirige lo que la app escribe en %LOCALAPPDATA% a una copia privada que el Bloc de notas y
    /// el Explorador no ven (no encontrarían pronunciacion.json). El LocalState del paquete es una carpeta real para todos.
    /// </summary>
    internal static string DefaultRoot(string localAppData, string? packageFamilyName) =>
        packageFamilyName is null
            ? Path.Combine(localAppData, "Shoken")
            : Path.Combine(localAppData, "Packages", packageFamilyName, "LocalState");

    private static string? CurrentPackageFamilyName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer)
        {
            return null; // APPMODEL_ERROR_NO_PACKAGE: sin identidad de paquete
        }

        var name = new char[length];
        return GetCurrentPackageFamilyName(ref length, name) == 0 ? new string(name, 0, (int)length - 1) : null;
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);

    public string Root { get; } = root;
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string UsageFile => Path.Combine(Root, "uso-azure.json");
    public string GoogleUsageFile => Path.Combine(Root, "uso-google.json");
    public string PronunciationFile => Path.Combine(Root, "pronunciacion.json");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string ViewDirectory => Path.Combine(Root, "view");
    public string WebViewDataDirectory => Path.Combine(Root, "webview");
    public string TempDirectory => Path.Combine(Root, "temp");
    public string ErrorLogFile => Path.Combine(Root, "errores.log");

    /// <summary>Texto Markdown pegado en pestañas; cada archivo se borra al cerrar su pestaña.</summary>
    public string PastedDirectory => Path.Combine(Root, "pegados");

    /// <summary>Apuntes de cada documento (un archivo por documento).</summary>
    public string NotesDirectory => Path.Combine(Root, "apuntes");

    /// <summary>Qué bloques de cada documento se escucharon (un archivo por documento).</summary>
    public string HeardDirectory => Path.Combine(Root, "escuchado");

    /// <summary>
    /// Imágenes de los documentos de Word abiertos, con el SHA-256 de su contenido como nombre (las sirve img.lector).
    /// No se limpia sola, como la caché de audio.
    /// </summary>
    public string WordImagesDirectory => Path.Combine(Root, "imagenes-word");

    /// <summary>Modelo de Kokoro (unos 310 MB), descargado desde Configuración.</summary>
    public string KokoroModelFile => Path.Combine(Root, "kokoro", "kokoro.onnx");

    /// <summary>Programa de Piper y sus voces, descargados desde Configuración (<see cref="Tts.PiperDownloads"/>).</summary>
    public string PiperDirectory => Path.Combine(Root, "piper");

    /// <summary>Cuenta de Azure con la que se consulta el consumo (sin secretos: el token va en la caché cifrada).</summary>
    public string AzureAccountFile => Path.Combine(Root, "cuenta-azure.json");

    public void EnsureCreated()
    {
        foreach (string dir in new[] { Root, CacheDirectory, ViewDirectory, WebViewDataDirectory, TempDirectory, PastedDirectory, WordImagesDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
