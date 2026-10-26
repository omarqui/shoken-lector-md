using System.Security.Cryptography;

namespace Shoken.Core.Tts;

/// <summary>Un archivo para descargar: de dónde, cuánto pesa y su huella SHA-256 (en hexadecimal) para comprobarlo.</summary>
public sealed record RemoteFile(Uri Url, long Bytes, string? Sha256 = null);

/// <summary>Descarga de modelos y programas de los motores locales a la carpeta de datos de la app.</summary>
public static class FileDownloader
{
    /// <summary>
    /// Descarga a un ".part" y solo con el tamaño y la huella exactos lo renombra: un corte o un archivo distinto nunca
    /// deja uno roto en su lugar.
    /// </summary>
    /// <param name="progress">De 0 a 1.</param>
    public static async Task DownloadAsync(HttpClient http, RemoteFile file, string targetPath,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string partial = targetPath + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            using (HttpResponseMessage response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream target = File.Create(partial);
                var buffer = new byte[81920];
                double reported = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    double fraction = Math.Min(1, (double)total / file.Bytes);
                    if (fraction - reported >= 0.01 || fraction >= 1)
                    {
                        progress?.Report(fraction);
                        reported = fraction;
                    }
                }
            }

            if (total != file.Bytes)
            {
                throw new IOException($"La descarga quedó incompleta ({total:N0} de {file.Bytes:N0} bytes). Vuelve a intentarlo.");
            }

            if (file.Sha256 is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Lo descargado de {file.Url.Host} no es el archivo esperado (otra huella SHA-256).");
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
