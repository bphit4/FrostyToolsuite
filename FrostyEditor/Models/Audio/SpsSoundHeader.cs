using System;
using System.Buffers.Binary;
using System.IO;

namespace FrostyEditor.Models.Audio;

public sealed class SpsSoundHeader
{
    public byte Version { get; set; }
    public byte Codec { get; set; }
    public byte ChannelConfig { get; set; }
    public int SampleRate { get; set; }
    public byte Type { get; set; }
    public bool Loop { get; set; }
    public int SamplesCount { get; set; }

    public float DurationInSeconds => SampleRate == 0 ? 0f : (float)SamplesCount / SampleRate;
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationInSeconds);

    public SpsSoundHeader()
    {
    }

    public SpsSoundHeader(int header1, int header2)
    {
        Version = (byte)((header1 >> 28) & 0xF);
        Codec = (byte)((header1 >> 24) & 0xF);
        ChannelConfig = (byte)((header1 >> 18) & 0x3F);
        SampleRate = header1 & 0x3FFFF;
        Type = (byte)((header2 >> 30) & 3);
        Loop = ((header2 >> 29) & 1) == 1;
        SamplesCount = header2 & 0x1FFFFFFF;
    }

    public static SpsSoundHeader LoadFrom(ReadOnlySpan<byte> span)
    {
        if (span.Length < 12)
        {
            throw new ArgumentException("Span must contain at least 12 bytes.", nameof(span));
        }

        int header1 = BinaryPrimitives.ReadInt32BigEndian(span.Slice(4, 4));
        int header2 = BinaryPrimitives.ReadInt32BigEndian(span.Slice(8, 4));
        return new SpsSoundHeader(header1, header2);
    }

    public static SpsSoundHeader LoadFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buffer = stackalloc byte[12];
        stream.ReadExactly(buffer);
        return LoadFrom(buffer);
    }
}
