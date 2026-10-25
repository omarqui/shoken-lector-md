using System.Security.Cryptography;
using System.Text;

namespace Shoken.Core.Settings;

/// <summary>Nombre del archivo de datos de un documento (apuntes, cobertura): lo calcula la ruta, no el contenido.</summary>
internal static class DocumentFiles
{
    /// <summary>SHA-256 de la ruta completa en mayúsculas, 20 hex en minúscula + <c>.json</c>, dentro de <paramref name="directory"/>.</summary>
    public static string PathFor(string directory, string documentPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(documentPath).ToUpperInvariant()));
        return Path.Combine(directory, Convert.ToHexString(hash)[..20].ToLowerInvariant() + ".json");
    }
}
