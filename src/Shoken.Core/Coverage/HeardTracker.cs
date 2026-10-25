namespace Shoken.Core.Coverage;

/// <summary>
/// Decide cuándo un segmento sonó completo y sin saltos. Un segmento cuenta si la lectura lo empezó desde su
/// principio y pasó al siguiente (o terminó el documento) sin que nadie la moviera de lugar.
/// </summary>
public sealed class HeardTracker
{
    private int _current = -1;
    private bool _fromBeginning;
    private int _jumpedTo = -1; // a dónde cayó el último salto, mientras la lectura no lo haya pasado

    /// <summary>El segmento que acaba de escucharse completo.</summary>
    public event Action<int>? SegmentHeard;

    public void Started(int segmentId, bool fromBeginning)
    {
        _current = segmentId;
        _fromBeginning = fromBeginning;
        _jumpedTo = -1;
    }

    /// <summary>La lectura avanzó sola a otro segmento.</summary>
    public void Moved(int segmentId)
    {
        if (_current < 0 || segmentId == _current)
        {
            return;
        }

        // Justo después de un salto, un tick puede leer todavía la posición de antes y la lectura parece avanzar
        // hasta el párrafo al que se saltó: ninguno de esos pasos empieza un párrafo desde su principio.
        if (_jumpedTo >= 0 && segmentId <= _jumpedTo)
        {
            _current = segmentId;
            _fromBeginning = false;
            return;
        }

        _jumpedTo = -1;
        if (segmentId == _current + 1 && _fromBeginning)
        {
            SegmentHeard?.Invoke(_current);
        }

        _fromBeginning = segmentId == _current + 1;
        _current = segmentId;
    }

    /// <summary>Alguien movió la lectura (saltar 10 s, tocar un párrafo): lo que sigue no cuenta desde el principio.</summary>
    public void Jumped(int landedSegmentId)
    {
        _current = landedSegmentId;
        _fromBeginning = false;
        _jumpedTo = landedSegmentId;
    }

    public void Reset()
    {
        _current = -1;
        _jumpedTo = -1;
    }

    /// <summary>El documento terminó: el último segmento también cuenta.</summary>
    public void Finished()
    {
        if (_current >= 0 && _fromBeginning)
        {
            SegmentHeard?.Invoke(_current);
        }

        Reset();
    }
}
