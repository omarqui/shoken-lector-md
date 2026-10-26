using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Shoken.Core.Settings;
using Shoken.Core.Tts;

namespace Shoken.Core.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shoken-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Sirve un archivo por dirección y anota qué se pidió.</summary>
    private sealed class FilesHandler(Dictionary<Uri, byte[]> files) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(files.TryGetValue(request.RequestUri!, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static RemoteFile Remote(string name, byte[] body) =>
        new(new Uri("https://ejemplo/" + name), body.Length, Convert.ToHexStringLower(SHA256.HashData(body)));

    // ------------------------------------------------------------------ descarga de un archivo

    [Fact]
    public async Task Download_WritesTheFile_ReportsProgress_AndLeavesNoPartialFile()
    {
        byte[] body = new byte[5000];
        RemoteFile file = Remote("kokoro.onnx", body);
        string target = Path.Combine(_dir, "kokoro", "kokoro.onnx");
        var reported = new List<double>();
        using var http = new HttpClient(new FilesHandler(new() { [file.Url] = body }));

        await FileDownloader.DownloadAsync(http, file, target, new Progress<double>(reported.Add), CancellationToken.None);

        Assert.Equal(5000, new FileInfo(target).Length);
        Assert.False(File.Exists(target + ".part"));
        await Task.Delay(50); // Progress<T> avisa en el contexto de sincronización
        Assert.Contains(1.0, reported);
    }

    [Fact]
    public async Task Download_WithTheWrongSize_KeepsNoBrokenFile()
    {
        var file = new RemoteFile(new Uri("https://ejemplo/kokoro.onnx"), 5000);
        string target = Path.Combine(_dir, "kokoro", "kokoro.onnx");
        using var http = new HttpClient(new FilesHandler(new() { [file.Url] = new byte[100] }));

        await Assert.ThrowsAsync<IOException>(() => FileDownloader.DownloadAsync(http, file, target, null, CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Download_WithAnotherFingerprint_KeepsNoFile()
    {
        // Mismo tamaño, otro contenido: el SHA-256 no coincide.
        RemoteFile file = Remote("voz.onnx", [1, 2, 3]);
        string target = Path.Combine(_dir, "voz.onnx");
        using var http = new HttpClient(new FilesHandler(new() { [file.Url] = [9, 9, 9] }));

        await Assert.ThrowsAsync<IOException>(() => FileDownloader.DownloadAsync(http, file, target, null, CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    // ------------------------------------------------------------------ Piper

    private static byte[] Zip(params string[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(entry);
            }
        }

        return memory.ToArray();
    }

    private static readonly byte[] ProgramZip = Zip("piper/piper.exe", "piper/onnxruntime.dll", "piper/espeak-ng-data/es_dict");

    private sealed record PiperSetup(PiperDownloads Downloads, PiperVoice Voice, FilesHandler Handler, HttpClient Http);

    private PiperSetup Piper(byte[]? programZip = null)
    {
        byte[] zip = programZip ?? ProgramZip;
        byte[] model = [1, 2, 3, 4], config = "{}"u8.ToArray();
        RemoteFile program = Remote("piper.zip", zip);
        var voice = new PiperVoice("es_MX-prueba-high", "Prueba", "mujer", Remote("prueba.onnx", model), Remote("prueba.onnx.json", config));
        var handler = new FilesHandler(new() { [program.Url] = zip, [voice.Model.Url] = model, [voice.Config.Url] = config });
        return new PiperSetup(new PiperDownloads(Path.Combine(_dir, "piper"), program), voice, handler, new HttpClient(handler));
    }

    [Fact]
    public async Task Piper_DownloadsTheProgramAndTheVoice_IntoTheDataFolder()
    {
        PiperSetup piper = Piper();
        var reported = new List<double>();

        await piper.Downloads.DownloadAsync(piper.Http, piper.Voice, everything: false, new Progress<double>(reported.Add), CancellationToken.None);

        Assert.True(piper.Downloads.HasProgram);
        Assert.True(piper.Downloads.HasVoice(piper.Voice.Id));
        Assert.Equal(Path.Combine(_dir, "piper", "programa", "piper.exe"), piper.Downloads.ExePath);
        Assert.True(File.Exists(Path.Combine(_dir, "piper", "programa", "espeak-ng-data", "es_dict")), "espeak-ng-data junto a piper.exe");
        Assert.True(File.Exists(piper.Downloads.ModelPath(piper.Voice.Id) + ".json"), "el .onnx.json junto al modelo");
        Assert.Equal(["programa", "voces"], Directory.GetFileSystemEntries(Path.Combine(_dir, "piper")).Select(Path.GetFileName).Order());
        await Task.Delay(50);
        Assert.Contains(1.0, reported);
    }

    [Fact]
    public async Task Piper_WithTheProgramInstalled_OnlyDownloadsTheMissingVoice()
    {
        PiperSetup piper = Piper();
        await piper.Downloads.DownloadAsync(piper.Http, piper.Voice, everything: false, null, CancellationToken.None);
        var other = piper.Voice with { Id = "es_MX-otra-medium" };
        piper.Handler.Requested.Clear();

        Assert.Equal(other.Model.Bytes + other.Config.Bytes, piper.Downloads.BytesToDownload(other, everything: false));
        await piper.Downloads.DownloadAsync(piper.Http, other, everything: false, null, CancellationToken.None);

        Assert.Equal([other.Config.Url, other.Model.Url], piper.Handler.Requested);
        Assert.True(piper.Downloads.HasVoice(other.Id));
        Assert.Equal(0, piper.Downloads.BytesToDownload(other, everything: false));
    }

    [Fact]
    public async Task Piper_WithoutPiperExeInTheZip_KeepsTheProgramItHad()
    {
        PiperSetup good = Piper();
        await good.Downloads.DownloadAsync(good.Http, good.Voice, everything: false, null, CancellationToken.None);
        PiperSetup broken = Piper(Zip("piper/LEEME.txt"));

        await Assert.ThrowsAsync<IOException>(() =>
            broken.Downloads.DownloadAsync(broken.Http, broken.Voice, everything: true, null, CancellationToken.None));

        Assert.Equal("piper/piper.exe", File.ReadAllText(good.Downloads.ExePath));
        Assert.Equal(["programa", "voces"], Directory.GetFileSystemEntries(Path.Combine(_dir, "piper")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Piper_InUse_SaysSo_AndKeepsTheProgramItHad()
    {
        // La app deja piper.exe corriendo mientras lee: Windows no deja mover su carpeta.
        PiperSetup piper = Piper();
        await piper.Downloads.DownloadAsync(piper.Http, piper.Voice, everything: false, null, CancellationToken.None);

        using (new FileStream(piper.Downloads.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                piper.Downloads.DownloadAsync(piper.Http, piper.Voice, everything: true, null, CancellationToken.None));
            Assert.Contains("en uso", error.Message);
        }

        Assert.True(piper.Downloads.HasProgram);
        Assert.Equal(["programa", "voces"], Directory.GetFileSystemEntries(Path.Combine(_dir, "piper")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Piper_OffersTwoMexicanVoices_AndDefaultsToClaude()
    {
        Assert.Equal(["es_MX-claude-high", "es_MX-ald-medium"], PiperDownloads.Voices.Select(v => v.Id));
        Assert.All(PiperDownloads.Voices, v => Assert.Equal(64, v.Model.Sha256!.Length));
        Assert.Equal("es_MX-claude-high", new AppSettings().PiperVoice);
        Assert.Equal(Path.Combine(_dir, "piper"), new AppPaths(_dir).PiperDirectory);
    }

    [Fact]
    public async Task Piper_UsesTheDownloadedOne_AndOnlyWithoutItTheOldPaths()
    {
        var paths = new AppPaths(_dir);
        Directory.CreateDirectory(_dir);
        string oldExe = Path.Combine(_dir, "viejo-piper.exe"), oldModel = Path.Combine(_dir, "viejo.onnx");
        File.WriteAllText(oldExe, "");
        File.WriteAllText(oldModel, "");
        var settings = new AppSettings { PiperExePath = oldExe, PiperModelPath = oldModel, PiperVoice = "es_MX-claude-high" };

        Assert.Equal(new PiperFiles(oldExe, oldModel), EngineServices.ResolvePiper(settings, paths));

        PiperVoice claude = PiperDownloads.Voices[0];
        byte[] zip = ProgramZip, model = [1], config = "{}"u8.ToArray();
        var downloads = new PiperDownloads(paths.PiperDirectory, new RemoteFile(new Uri("https://ejemplo/piper.zip"), zip.Length));
        var voice = claude with { Model = new RemoteFile(new Uri("https://ejemplo/m"), 1), Config = new RemoteFile(new Uri("https://ejemplo/c"), config.Length) };
        using var http = new HttpClient(new FilesHandler(new()
        {
            [new Uri("https://ejemplo/piper.zip")] = zip, [voice.Model.Url] = model, [voice.Config.Url] = config,
        }));
        await downloads.DownloadAsync(http, voice, everything: false, null, CancellationToken.None);

        Assert.Equal(new PiperFiles(downloads.ExePath, downloads.ModelPath(claude.Id)), EngineServices.ResolvePiper(settings, paths));

        // Otra voz elegida que no se descargó: no hay Piper (la ventana de Configuración avisa al guardar).
        settings.PiperVoice = "es_MX-ald-medium";
        Assert.Null(EngineServices.ResolvePiper(settings, paths));
        Assert.Null(EngineServices.ResolvePiper(new AppSettings(), new AppPaths(Path.Combine(_dir, "vacio"))));
    }
}
