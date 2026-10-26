using System.IO.Compression;

namespace Shoken.Core.Tts;

/// <summary>Voz de Piper que se puede descargar: el modelo .onnx y su configuración .onnx.json.</summary>
public sealed record PiperVoice(string Id, string Name, string Kind, RemoteFile Model, RemoteFile Config);

/// <summary>Lo que Piper necesita para leer: el programa y el modelo de voz (con su .onnx.json al lado).</summary>
public sealed record PiperFiles(string ExePath, string ModelPath);

/// <summary>
/// Piper sin configurar nada: descarga el programa oficial para Windows (piper.exe con sus DLL y espeak-ng-data) y
/// las voces es_MX a la carpeta de datos de la app, comprobados por su SHA-256.
/// </summary>
public sealed class PiperDownloads
{
    // Fijas en una revisión del repositorio: lo que se baja no cambia aunque lo actualicen.
    private const string VoicesBase =
        "https://huggingface.co/rhasspy/piper-voices/resolve/5041a7a49ae5b3a0f30e6837769eb03fdbb231bd/es/es_MX/";

    /// <summary>Último programa de Piper para Windows (2023.11.14-2, MIT; trae espeak-ng, GPL-3).</summary>
    public static RemoteFile OfficialProgram { get; } = new(
        new Uri("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip"),
        22_477_236,
        "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea");

    /// <summary>Voces es_MX: Claude (mujer, combina con Dalia) y Ald (hombre, combina con Jorge).</summary>
    public static IReadOnlyList<PiperVoice> Voices { get; } =
    [
        Voice("es_MX-claude-high", "Claude", "mujer", "claude/high/",
            63_122_309, "3ef40a71ea63852cd8ab7e6fa7d2ecdcfa67a0b47c9c48e3f10e02ee02083ea0",
            4_963, "1afc81f703c0e4cb3b4d7c0dca096b8b54a98806807f0170cf5eb5557723c12d"),
        Voice("es_MX-ald-medium", "Ald", "hombre", "ald/medium/",
            63_201_294, "019b3803293c93e34a206dd2e53a3889209a514e786fd7144f7b70196c579b63",
            4_878, "5a71498158e04afc8099bfd019c7e87c68eb9d042505a2b1a87e5c1ac2b1a61d"),
    ];

    private readonly string _directory;
    private readonly RemoteFile _program;

    /// <param name="directory">Carpeta de Piper en los datos de la app (<see cref="Settings.AppPaths.PiperDirectory"/>).</param>
    /// <param name="program">Para las pruebas; por omisión, <see cref="OfficialProgram"/>.</param>
    public PiperDownloads(string directory, RemoteFile? program = null)
    {
        _directory = directory;
        _program = program ?? OfficialProgram;
    }

    private string ProgramDirectory => Path.Combine(_directory, "programa");

    public string ExePath => Path.Combine(ProgramDirectory, "piper.exe");

    public string ModelPath(string voiceId) => Path.Combine(_directory, "voces", voiceId + ".onnx");

    public bool HasProgram => File.Exists(ExePath);

    public bool HasVoice(string voiceId) => File.Exists(ModelPath(voiceId)) && File.Exists(ModelPath(voiceId) + ".json");

    /// <summary>Lo que falta bajar para leer con <paramref name="voice"/>; todo si <paramref name="everything"/> (volver a descargar).</summary>
    public long BytesToDownload(PiperVoice voice, bool everything) => Steps(voice, everything).Sum(s => s.File.Bytes);

    /// <param name="progress">De 0 a 1, sobre todo lo que se baja.</param>
    public async Task DownloadAsync(HttpClient http, PiperVoice voice, bool everything, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var steps = Steps(voice, everything);
        double total = Math.Max(1, steps.Sum(s => s.File.Bytes));
        long done = 0;
        foreach ((RemoteFile file, DownloadStep run) in steps)
        {
            long before = done;
            var relay = new Relay(fraction => progress?.Report((before + fraction * file.Bytes) / total));
            await run(http, relay, cancellationToken).ConfigureAwait(false);
            done += file.Bytes;
        }
    }

    private delegate Task DownloadStep(HttpClient http, IProgress<double> progress, CancellationToken cancellationToken);

    private List<(RemoteFile File, DownloadStep Run)> Steps(PiperVoice voice, bool everything)
    {
        var steps = new List<(RemoteFile, DownloadStep)>();
        if (everything || !HasProgram)
        {
            steps.Add((_program, InstallProgramAsync));
        }

        if (everything || !HasVoice(voice.Id))
        {
            string model = ModelPath(voice.Id);
            steps.Add((voice.Config, (http, progress, ct) => FileDownloader.DownloadAsync(http, voice.Config, model + ".json", progress, ct)));
            steps.Add((voice.Model, (http, progress, ct) => FileDownloader.DownloadAsync(http, voice.Model, model, progress, ct)));
        }

        return steps;
    }

    /// <summary>
    /// Descomprime aparte y solo con piper.exe adentro reemplaza el programa: una descarga rota no deja a Piper sin
    /// programa. Si Piper está leyendo, Windows no deja mover su carpeta y el que había se queda.
    /// </summary>
    private async Task InstallProgramAsync(HttpClient http, IProgress<double> progress, CancellationToken cancellationToken)
    {
        string zip = Path.Combine(_directory, "programa.zip");
        string staging = Path.Combine(_directory, "programa.nuevo");
        string old = Path.Combine(_directory, "programa.viejo");
        await FileDownloader.DownloadAsync(http, _program, zip, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            DeleteDirectory(staging);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging), cancellationToken).ConfigureAwait(false);
            string extracted = Path.Combine(staging, "piper"); // el zip trae todo dentro de piper\
            if (!File.Exists(Path.Combine(extracted, "piper.exe")))
            {
                throw new IOException("Lo descargado no trae piper.exe.");
            }

            DeleteDirectory(old);
            if (Directory.Exists(ProgramDirectory))
            {
                try
                {
                    Directory.Move(ProgramDirectory, old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Piper está en uso. Sal de Shoken (Salir, en el ícono de la bandeja), ábrelo y vuelve a descargarlo.", ex);
                }
            }

            Directory.Move(extracted, ProgramDirectory);
        }
        finally
        {
            TryDelete(() => File.Delete(zip));
            TryDelete(() => DeleteDirectory(staging));
            TryDelete(() => DeleteDirectory(old));
        }
    }

    private static PiperVoice Voice(string id, string name, string kind, string folder,
        long modelBytes, string modelSha256, long configBytes, string configSha256) =>
        new(id, name, kind,
            new RemoteFile(new Uri(VoicesBase + folder + id + ".onnx"), modelBytes, modelSha256),
            new RemoteFile(new Uri(VoicesBase + folder + id + ".onnx.json"), configBytes, configSha256));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Avisa al momento (sin pasar por el contexto de sincronización, como <see cref="Progress{T}"/>).</summary>
    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
