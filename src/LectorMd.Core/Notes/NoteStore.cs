using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LectorMd.Core.Notes;

/// <summary>
/// Apuntes de cada documento, en la carpeta de datos de la app (no junto al .md: no ensucian el repositorio).
/// Un archivo por documento, nombrado por la ruta (sin distinguir mayúsculas); si se mueve el .md, no lo siguen.
/// </summary>
public sealed class NoteStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public List<DocumentNote> Load(string documentPath)
    {
        string file = FileFor(documentPath);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<NoteFile>(File.ReadAllText(file), Json)?.Notes ?? [] : [];
        }
        catch (JsonException)
        {
            return []; // un archivo dañado no impide abrir el documento
        }
    }

    /// <summary>Sin apuntes, el archivo se borra.</summary>
    public void Save(string documentPath, IReadOnlyList<DocumentNote> notes)
    {
        string file = FileFor(documentPath);
        if (notes.Count == 0)
        {
            Delete(documentPath);
            return;
        }

        Directory.CreateDirectory(directory);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new NoteFile(documentPath, notes.ToList()), Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Un texto pegado que se guardó como archivo: sus apuntes pasan al archivo.</summary>
    public void Move(string fromPath, string toPath)
    {
        List<DocumentNote> notes = Load(fromPath);
        Delete(fromPath);
        Save(toPath, notes);
    }

    public void Delete(string documentPath)
    {
        string file = FileFor(documentPath);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string FileFor(string documentPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(documentPath).ToUpperInvariant()));
        return Path.Combine(directory, Convert.ToHexString(hash)[..20].ToLowerInvariant() + ".json");
    }

    private sealed record NoteFile(string Document, List<DocumentNote> Notes);
}
