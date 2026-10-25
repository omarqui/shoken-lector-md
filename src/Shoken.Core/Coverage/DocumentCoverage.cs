using Shoken.Core.Model;

namespace Shoken.Core.Coverage;

/// <summary>Cómo sonó un bloque escuchado: completo o solo anunciado («Fórmula.», «Bloque de código…»).</summary>
public enum HeardSpoken
{
    Full,
    Announcement,
}

/// <summary>Un bloque escuchado, reconocido por <see cref="SpeechSegment.BlockKey"/> (el texto del documento, no el que se dice).</summary>
public sealed record HeardBlock(string Key, HeardSpoken Spoken);

/// <summary>
/// Qué bloques de la página se escucharon. La unidad es el bloque (los segmentos que comparten <c>AnchorId</c>):
/// queda escuchado cuando todos sus segmentos sonaron, o desde el inicio si su clave estaba guardada.
/// Como se reconoce por clave, sobrevive a mover el bloque y se pierde solo en el que se editó.
/// </summary>
public sealed class DocumentCoverage
{
    private readonly SpeechDocument _document;
    private readonly Dictionary<string, List<int>> _segmentsByAnchor = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heardAnchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heardNow = new(StringComparer.Ordinal); // los que sonaron en esta sesión
    private readonly Dictionary<string, HeardSpoken> _stored = new(StringComparer.Ordinal);
    private readonly HashSet<int> _marked = [];

    public DocumentCoverage(SpeechDocument document, IEnumerable<HeardBlock> stored)
    {
        _document = document;
        foreach (SpeechSegment segment in document.Segments)
        {
            if (!_segmentsByAnchor.TryGetValue(segment.AnchorId, out List<int>? ids))
            {
                _segmentsByAnchor[segment.AnchorId] = ids = [];
            }

            ids.Add(segment.Id);
        }

        foreach (HeardBlock block in stored)
        {
            _stored.TryAdd(block.Key, block.Spoken);
        }

        foreach ((string anchor, List<int> ids) in _segmentsByAnchor)
        {
            string key = document.Segments[ids[0]].BlockKey;
            if (key.Length > 0 && _stored.ContainsKey(key))
            {
                _heardAnchors.Add(anchor);
            }
        }
    }

    public IReadOnlyCollection<string> HeardAnchors => _heardAnchors;

    /// <summary>Los segmentos de los bloques escuchados.</summary>
    public IReadOnlySet<int> HeardSegments =>
        _heardAnchors.SelectMany(a => _segmentsByAnchor[a]).ToHashSet();

    public bool IsBlockHeard(string anchorId) => _heardAnchors.Contains(anchorId);

    /// <returns>
    /// True si cambió lo que se guarda: con este segmento un bloque quedó escuchado y antes no lo estaba, o uno que
    /// venía de lo guardado volvió a sonar distinto de como se guardó (completo y no solo anunciado, o al revés).
    /// </returns>
    public bool MarkSegmentHeard(int segmentId)
    {
        if (segmentId < 0 || segmentId >= _document.Segments.Count)
        {
            return false;
        }

        _marked.Add(segmentId);
        string anchor = _document.Segments[segmentId].AnchorId;
        if (!_segmentsByAnchor[anchor].All(_marked.Contains))
        {
            return false;
        }

        bool firstTimeNow = _heardNow.Add(anchor); // aunque ya estuviera guardado: volvió a sonar con las opciones de ahora
        if (_heardAnchors.Add(anchor))
        {
            return true;
        }

        List<int> ids = _segmentsByAnchor[anchor];
        return firstTimeNow && _stored.TryGetValue(_document.Segments[ids[0]].BlockKey, out HeardSpoken saved)
            && saved != SpokenNow(ids);
    }

    /// <summary>Cómo suena el bloque con las opciones de ahora.</summary>
    private HeardSpoken SpokenNow(List<int> ids) =>
        ids.All(id => _document.Segments[id].IsAnnouncement) ? HeardSpoken.Announcement : HeardSpoken.Full;

    /// <summary>
    /// Solo los bloques del documento actual (lo que ya no existe se descarta). Se guarda qué sonó: un bloque que
    /// solo viene de lo guardado conserva cómo sonó entonces; uno escuchado ahora, cómo suena con las opciones de ahora.
    /// </summary>
    public IReadOnlyList<HeardBlock> ToStored()
    {
        var blocks = new List<HeardBlock>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string anchor, List<int> ids) in _segmentsByAnchor)
        {
            string key = _document.Segments[ids[0]].BlockKey;
            if (!_heardAnchors.Contains(anchor) || key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            if (!_heardNow.Contains(anchor) && _stored.TryGetValue(key, out HeardSpoken saved))
            {
                blocks.Add(new HeardBlock(key, saved));
                continue;
            }

            blocks.Add(new HeardBlock(key, SpokenNow(ids)));
        }

        return blocks;
    }

    public void Clear()
    {
        _heardAnchors.Clear();
        _heardNow.Clear();
        _stored.Clear();
        _marked.Clear();
    }
}
