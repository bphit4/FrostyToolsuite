using System;
using Frosty.Sdk;
using Frosty.Sdk.IO;
using Frosty.Sdk.IO.Compression;
using Frosty.Sdk.Resources;
using Frosty.Sdk.Utils;

namespace Frosty.ModSupport;

public static class ModExportUtilities
{
    public static CompressionType GetEbxCompression() => GetConfiguredCompression(ProfilesLibrary.EbxCompression, CompressionType.LZ4);

    public static CompressionType GetResCompression() => GetConfiguredCompression(ProfilesLibrary.ResCompression, CompressionType.LZ4);

    public static CompressionType GetChunkCompression() => GetConfiguredCompression(ProfilesLibrary.ChunkCompression, CompressionType.LZ4);

    public static CompressionType GetTextureChunkCompression() => GetConfiguredCompression(ProfilesLibrary.TextureChunkCompression, GetChunkCompression());

    public static Block<byte> CompressTextureChunkData(Texture texture, Block<byte> rawData)
    {
        return CompressTextureChunkData(texture, rawData, GetTextureChunkCompression());
    }

    public static Block<byte> CompressTextureChunkData(Texture texture, Block<byte> rawData, CompressionType compressionType)
    {
        byte[] source = rawData.ToArray();
        uint first = 0;
        uint second = (uint)source.Length;

        if (texture.MipCount > 1 && source.Length > ProfilesLibrary.MaxBufferSize)
        {
            int index = 0;
            while (second > ProfilesLibrary.MaxBufferSize && index < texture.FirstMip)
            {
                first += texture.MipSizes[index];
                second -= texture.MipSizes[index++];
            }
        }

        if (texture.LogicalOffset != first || texture.LogicalSize != second)
        {
            texture.LogicalOffset = first;
            texture.LogicalSize = second;
        }

        texture.RangeStart = 0;
        texture.RangeEnd = 0;
        texture.FirstMipOffset = 0;
        texture.SecondMipOffset = 0;

        Block<byte> outData = new(0);
        using (BlockStream stream = new(outData, true))
        {
            if (first != 0)
            {
                using Block<byte> prefix = CompressTextureChunkSegment(source, 0, (int)first, texture, compressionType, 0);
                stream.Write(prefix);
                texture.RangeStart = (uint)stream.Position;
            }

            using Block<byte> tail = CompressTextureChunkSegment(source, (int)first, (int)second, texture, compressionType, first);
            stream.Write(tail);
            texture.RangeEnd = texture.RangeStart == 0 ? 0u : (uint)stream.Position;
        }

        return outData;
    }

    private static Block<byte> CompressTextureChunkSegment(byte[] source, int offset, int length, Texture texture, CompressionType compressionType, uint dataOffset)
    {
        Block<byte> outData = new(0);
        using (BlockStream stream = new(outData, true))
        {
            int total = 0;
            while (total < length)
            {
                int chunkLength = Math.Min(ProfilesLibrary.MaxBufferSize, length - total);
                byte[] chunk = new byte[chunkLength];
                Buffer.BlockCopy(source, offset + total, chunk, 0, chunkLength);

                using Block<byte> rawChunk = new(chunk);
                using Block<byte> compressedChunk = Cas.CompressData(rawChunk, compressionType, 0);
                stream.Write(compressedChunk);

                total += chunkLength;
                uint absoluteOffset = dataOffset + (uint)total;
                if (texture.MipSizes[0] != 0 && absoluteOffset == texture.MipSizes[0])
                {
                    texture.FirstMipOffset = (uint)stream.Position;
                    texture.SecondMipOffset = (uint)stream.Position;
                }
                else if (texture.MipSizes[1] != 0 && absoluteOffset == texture.MipSizes[0] + texture.MipSizes[1])
                {
                    texture.SecondMipOffset = (uint)stream.Position;
                }
            }
        }

        return outData;
    }

    private static CompressionType GetConfiguredCompression(CompressionType configured, CompressionType fallback)
    {
        return configured == CompressionType.None ? fallback : configured;
    }
}
