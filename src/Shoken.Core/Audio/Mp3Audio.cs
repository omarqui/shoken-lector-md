namespace Shoken.Core.Audio;

/// <summary>
/// Lo mínimo de MP3 que necesita la demo: medir la duración (Google no la informa) y unir bloques en un archivo.
/// Solo MPEG Layer III, que es lo que entregan Azure y Google.
/// </summary>
public static class Mp3Audio
{
    private static readonly int[] BitratesV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] BitratesV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>Duración sumando los cuadros; lo que no es un cuadro (etiquetas ID3, basura) se salta.</summary>
    public static TimeSpan Duration(ReadOnlySpan<byte> data)
    {
        long ticks = 0;
        int position = Id3Length(data);
        while (position + 4 <= data.Length)
        {
            if (TryReadFrame(data[position..], out int length, out int samples, out int sampleRate) && position + length <= data.Length)
            {
                ticks += samples * TimeSpan.TicksPerSecond / sampleRate;
                position += length;
            }
            else
            {
                position++;
            }
        }

        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>Une bloques en un solo archivo, sin las etiquetas ID3 del principio de cada uno.</summary>
    public static byte[] Concatenate(IEnumerable<byte[]> parts)
    {
        using var output = new MemoryStream();
        foreach (byte[] part in parts)
        {
            int skip = Id3Length(part);
            output.Write(part, skip, part.Length - skip);
        }

        return output.ToArray();
    }

    // Etiqueta ID3v2 al principio: "ID3", versión (2), banderas (1) y tamaño en 4 bytes de 7 bits.
    private static int Id3Length(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3')
        {
            return 0;
        }

        int size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        int footer = (data[5] & 0x10) != 0 ? 10 : 0;
        return Math.Min(data.Length, 10 + size + footer);
    }

    private static bool TryReadFrame(ReadOnlySpan<byte> data, out int length, out int samples, out int sampleRate)
    {
        length = samples = sampleRate = 0;
        if (data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        int version = (data[1] >> 3) & 3; // 0 = MPEG 2.5, 2 = MPEG 2, 3 = MPEG 1
        int layer = (data[1] >> 1) & 3;   // 1 = Layer III
        int bitrateIndex = data[2] >> 4;
        int rateIndex = (data[2] >> 2) & 3;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return false;
        }

        bool mpeg1 = version == 3;
        int bitrate = (mpeg1 ? BitratesV1 : BitratesV2)[bitrateIndex] * 1000;
        sampleRate = (version switch { 3 => new[] { 44100, 48000, 32000 }, 2 => [22050, 24000, 16000], _ => [11025, 12000, 8000] })[rateIndex];
        int padding = (data[2] >> 1) & 1;
        samples = mpeg1 ? 1152 : 576;
        length = (mpeg1 ? 144 : 72) * bitrate / sampleRate + padding;
        return length > 4;
    }
}
