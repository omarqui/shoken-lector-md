using System.Buffers.Binary;
using System.Text;

namespace LectorMd.Core.Audio;

/// <summary>Audio PCM sin comprimir (lectura/escritura de WAV y concatenación con silencios).</summary>
public sealed class WavAudio
{
    public WavAudio(int sampleRate, short channels, short bitsPerSample, byte[] pcm)
    {
        if (sampleRate <= 0 || channels <= 0 || bitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new ArgumentException("Formato WAV no soportado.");
        }

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        Pcm = pcm;
    }

    public int SampleRate { get; }
    public short Channels { get; }
    public short BitsPerSample { get; }
    public byte[] Pcm { get; }

    public int BlockAlign => Channels * BitsPerSample / 8;
    public int ByteRate => SampleRate * BlockAlign;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Pcm.Length / ByteRate);

    public bool SameFormatAs(WavAudio other) =>
        SampleRate == other.SampleRate && Channels == other.Channels && BitsPerSample == other.BitsPerSample;

    public static WavAudio Silence(int sampleRate, short channels, short bitsPerSample, int milliseconds) =>
        new(sampleRate, channels, bitsPerSample, new byte[SilenceBytes(sampleRate, channels, bitsPerSample, milliseconds)]);

    public static WavAudio Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("El archivo no es un WAV (RIFF/WAVE).");
        }

        int offset = 12;
        int sampleRate = 0;
        short channels = 0, bits = 0;
        byte[]? pcm = null;

        while (offset + 8 <= data.Length)
        {
            ReadOnlySpan<byte> id = data.Slice(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            int bodyStart = offset + 8;
            int available = data.Length - bodyStart;
            int bodyLength = size > (uint)available ? available : (int)size;

            if (id.SequenceEqual("fmt "u8))
            {
                ReadOnlySpan<byte> fmt = data.Slice(bodyStart, bodyLength);
                if (fmt.Length < 16)
                {
                    throw new InvalidDataException("Cabecera fmt incompleta.");
                }

                ushort format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                if (format is not (1 or 0xFFFE))
                {
                    throw new InvalidDataException($"Solo se admite PCM (formato {format}).");
                }

                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                // Algunos generadores en streaming escriben tamaño 0 o 0xFFFFFFFF: se toma lo que haya.
                int length = size == 0 || size == uint.MaxValue ? available : bodyLength;
                pcm = data.Slice(bodyStart, length).ToArray();
                break;
            }

            offset = bodyStart + bodyLength + (bodyLength % 2); // los chunks RIFF se alinean a 2 bytes
        }

        if (pcm is null || sampleRate == 0)
        {
            throw new InvalidDataException("WAV sin datos de audio.");
        }

        var audio = new WavAudio(sampleRate, channels, bits, pcm);
        int excess = pcm.Length % audio.BlockAlign;
        return excess == 0 ? audio : new WavAudio(sampleRate, channels, bits, pcm[..^excess]);
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[44 + Pcm.Length];
        Span<byte> h = bytes;
        Encoding.ASCII.GetBytes("RIFF", h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + Pcm.Length);
        Encoding.ASCII.GetBytes("WAVE", h[8..]);
        Encoding.ASCII.GetBytes("fmt ", h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], ByteRate);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], (short)BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], BitsPerSample);
        Encoding.ASCII.GetBytes("data", h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], Pcm.Length);
        Pcm.CopyTo(bytes, 44);
        return bytes;
    }

    /// <summary>
    /// Une varias partes (mismo formato) intercalando silencios. Devuelve el instante en que empieza
    /// cada parte, después de su silencio previo.
    /// </summary>
    public static WavAudio Concatenate(IReadOnlyList<(WavAudio Audio, int SilenceBeforeMs)> parts, out IReadOnlyList<TimeSpan> starts)
    {
        if (parts.Count == 0)
        {
            throw new ArgumentException("No hay partes que unir.", nameof(parts));
        }

        WavAudio format = parts[0].Audio;
        var output = new MemoryStream();
        var offsets = new List<TimeSpan>(parts.Count);

        foreach ((WavAudio audio, int silenceMs) in parts)
        {
            if (!audio.SameFormatAs(format))
            {
                throw new InvalidDataException("Las partes de audio tienen formatos distintos.");
            }

            int silence = SilenceBytes(format.SampleRate, format.Channels, format.BitsPerSample, silenceMs);
            if (silence > 0)
            {
                output.Write(new byte[silence]);
            }

            offsets.Add(TimeSpan.FromSeconds((double)output.Length / format.ByteRate));
            output.Write(audio.Pcm);
        }

        starts = offsets;
        return new WavAudio(format.SampleRate, format.Channels, format.BitsPerSample, output.ToArray());
    }

    private static int SilenceBytes(int sampleRate, short channels, short bitsPerSample, int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return 0;
        }

        int blockAlign = channels * bitsPerSample / 8;
        long frames = (long)sampleRate * milliseconds / 1000;
        return (int)(frames * blockAlign);
    }
}
