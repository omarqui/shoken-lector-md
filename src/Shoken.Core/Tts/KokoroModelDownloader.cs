namespace Shoken.Core.Tts;

/// <summary>Descarga del modelo de Kokoro (unos 310 MB) a la carpeta de datos de la app.</summary>
public static class KokoroModelDownloader
{
    /// <summary>Modelo de precisión completa publicado por KokoroSharp (Kokoro 82M v1.0, Apache 2.0).</summary>
    public static RemoteFile Model { get; } = new(
        new Uri("https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx"),
        325_508_342,
        "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05");

    /// <param name="progress">De 0 a 1.</param>
    public static Task DownloadAsync(HttpClient http, string targetPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
        FileDownloader.DownloadAsync(http, Model, targetPath, progress, cancellationToken);
}
