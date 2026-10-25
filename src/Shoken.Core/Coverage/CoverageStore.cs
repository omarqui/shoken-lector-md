using System.Text.Json;
using System.Text.Json.Serialization;
using Shoken.Core.Settings;

namespace Shoken.Core.Coverage;

/// <summary>
/// Lo escuchado de cada documento, en la carpeta de datos de la app. Un archivo por documento, nombrado por
/// la ruta como los apuntes (<see cref="DocumentFiles"/>).
/// </summary>
public sealed class CoverageStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<HeardBlock> Load(string documentPath)
    {
        string file = DocumentFiles.PathFor(directory, documentPath);
        try
        {
            // Un bloque nulo («"Blocks":[null]») o sin clave se descarta: no puede llegar a DocumentCoverage.
            return File.Exists(file)
                ? JsonSerializer.Deserialize<CoverageFile>(File.ReadAllText(file), Json)?.Blocks?
                    .Where(b => b is { Key: not null }).ToList() ?? []
                : [];
        }
        catch (JsonException)
        {
            return []; // un archivo dañado no impide abrir el documento
        }
    }

    /// <summary>Sin bloques, el archivo se borra.</summary>
    public void Save(string documentPath, IReadOnlyList<HeardBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            Delete(documentPath);
            return;
        }

        string file = DocumentFiles.PathFor(directory, documentPath);
        Directory.CreateDirectory(directory);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new CoverageFile(documentPath, blocks.ToList()), Json));
        File.Move(temp, file, overwrite: true);
    }

    public void Delete(string documentPath)
    {
        string file = DocumentFiles.PathFor(directory, documentPath);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private sealed record CoverageFile(string Document, List<HeardBlock> Blocks);
}
