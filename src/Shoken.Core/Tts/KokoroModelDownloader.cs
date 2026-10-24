namespace Shoken.Core.Tts;

/// <summary>Descarga del modelo de Kokoro (unos 310 MB) a la carpeta de datos de la app.</summary>
public static class KokoroModelDownloader
{
    /// <summary>Modelo de precisión completa publicado por KokoroSharp (Kokoro 82M v1.0, Apache 2.0).</summary>
    public static Uri ModelUrl { get; } = new("https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx");

    public const long ModelBytes = 325_508_342;

    /// <summary>
    /// Descarga a un ".part" y solo con el tamaño exacto lo renombra: un corte o un archivo distinto nunca deja
    /// un modelo roto en su lugar.
    /// </summary>
    /// <param name="progress">De 0 a 1.</param>
    public static async Task DownloadAsync(HttpClient http, Uri url, string targetPath, long expectedBytes,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string partial = targetPath + ".part";
        try
        {
            using (HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream target = File.Create(partial);
                var buffer = new byte[81920];
                long total = 0;
                double reported = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    double fraction = Math.Min(1, (double)total / expectedBytes);
                    if (fraction - reported >= 0.01 || fraction >= 1)
                    {
                        progress?.Report(fraction);
                        reported = fraction;
                    }
                }
            }

            long size = new FileInfo(partial).Length;
            if (size != expectedBytes)
            {
                throw new IOException($"La descarga quedó incompleta ({size:N0} de {expectedBytes:N0} bytes). Vuelve a intentarlo.");
            }

            File.Move(partial, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
