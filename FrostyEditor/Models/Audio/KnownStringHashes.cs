using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyEditor.Models.Audio;

public static class KnownStringHashes
{
    public static readonly Dictionary<uint, string> NewWaveHashLookup = BuildLookup();
    private static readonly Dictionary<string, uint[]> s_newWaveNameHashes = BuildNameLookup();

    private static Dictionary<uint, string> BuildLookup()
    {
        Dictionary<uint, string> lookup = new();

        Add(lookup, "Chunks");
        Add(lookup, "ChunkId");
        Add(lookup, "ChunkIndex");
        Add(lookup, "ChunkSize");
        Add(lookup, "DataBlock");
        Add(lookup, "Duration");
        Add(lookup, "FirstLoopSegmentIndex");
        Add(lookup, "FirstSegmentIndex");
        Add(lookup, "LastLoopSegmentIndex");
        Add(lookup, "MemoryChunkIndex");
        Add(lookup, "Persistence");
        Add(lookup, "ProjectKey");
        Add(lookup, "SegmentIndex");
        Add(lookup, "Selection");
        Add(lookup, "SelectionParameterId");
        Add(lookup, "SelectionParameterIndex");
        Add(lookup, "SelectionParameters");
        Add(lookup, "SegmentCount");
        Add(lookup, "Segments");
        Add(lookup, "SeekTableOffset");
        Add(lookup, "SamplesOffset");
        Add(lookup, "StreamChunkIndex");
        Add(lookup, "VariationId");
        Add(lookup, "VariationIndex");
        Add(lookup, "Variations");

        // Madden 26+ NewWave datasets/fields use alternate hashes for several bank tables.
        AddAlias(lookup, "Chunks", 0xA28D4A0D);
        AddAlias(lookup, "ChunkId", 0xF4369173);
        AddAlias(lookup, "ChunkIndex", 0x603CFF80);
        AddAlias(lookup, "ChunkSize", 0xDC19107B);

        AddAlias(lookup, "Segments", 0x3FE2AFD5);
        AddAlias(lookup, "Duration", 0x6CFCCE5B);
        AddAlias(lookup, "SeekTableOffset", 0xD506D74E);
        AddAlias(lookup, "SamplesOffset", 0xE8E591DD);
        AddAlias(lookup, "SegmentIndex", 0xD06D5A58);

        AddAlias(lookup, "Variations", 0xA29AF127);
        AddAlias(lookup, "FirstLoopSegmentIndex", 0x03DA5B4E);
        AddAlias(lookup, "MemoryChunkIndex", 0x4E5B3721);
        AddAlias(lookup, "LastLoopSegmentIndex", 0x65610234);
        AddAlias(lookup, "StreamChunkIndex", 0xB7126493);
        AddAlias(lookup, "SegmentCount", 0xD00A6005);
        AddAlias(lookup, "FirstSegmentIndex", 0xE4660A62);
        AddAlias(lookup, "VariationId", 0xF5F914D9);
        AddAlias(lookup, "VariationIndex", 0x6AC4E4EA);

        return lookup;
    }

    private static Dictionary<string, uint[]> BuildNameLookup()
    {
        Dictionary<string, List<uint>> lookup = new(StringComparer.OrdinalIgnoreCase);

        AddName(lookup, "Chunks");
        AddName(lookup, "ChunkId");
        AddName(lookup, "ChunkIndex");
        AddName(lookup, "ChunkSize");
        AddName(lookup, "DataBlock");
        AddName(lookup, "Duration");
        AddName(lookup, "FirstLoopSegmentIndex");
        AddName(lookup, "FirstSegmentIndex");
        AddName(lookup, "LastLoopSegmentIndex");
        AddName(lookup, "MemoryChunkIndex");
        AddName(lookup, "Persistence");
        AddName(lookup, "ProjectKey");
        AddName(lookup, "SegmentIndex");
        AddName(lookup, "Selection");
        AddName(lookup, "SelectionParameterId");
        AddName(lookup, "SelectionParameterIndex");
        AddName(lookup, "SelectionParameters");
        AddName(lookup, "SegmentCount");
        AddName(lookup, "Segments");
        AddName(lookup, "SeekTableOffset");
        AddName(lookup, "SamplesOffset");
        AddName(lookup, "StreamChunkIndex");
        AddName(lookup, "VariationId");
        AddName(lookup, "VariationIndex");
        AddName(lookup, "Variations");

        AddName(lookup, "Chunks", 0xA28D4A0D);
        AddName(lookup, "ChunkId", 0xF4369173);
        AddName(lookup, "ChunkIndex", 0x603CFF80);
        AddName(lookup, "ChunkSize", 0xDC19107B);

        AddName(lookup, "Segments", 0x3FE2AFD5);
        AddName(lookup, "Duration", 0x6CFCCE5B);
        AddName(lookup, "SeekTableOffset", 0xD506D74E);
        AddName(lookup, "SamplesOffset", 0xE8E591DD);
        AddName(lookup, "SegmentIndex", 0xD06D5A58);

        AddName(lookup, "Variations", 0xA29AF127);
        AddName(lookup, "FirstLoopSegmentIndex", 0x03DA5B4E);
        AddName(lookup, "MemoryChunkIndex", 0x4E5B3721);
        AddName(lookup, "LastLoopSegmentIndex", 0x65610234);
        AddName(lookup, "StreamChunkIndex", 0xB7126493);
        AddName(lookup, "SegmentCount", 0xD00A6005);
        AddName(lookup, "FirstSegmentIndex", 0xE4660A62);
        AddName(lookup, "VariationId", 0xF5F914D9);
        AddName(lookup, "VariationIndex", 0x6AC4E4EA);

        return lookup.ToDictionary(static pair => pair.Key, static pair => pair.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<uint> ResolveHashes(string value)
    {
        if (s_newWaveNameHashes.TryGetValue(value, out uint[]? hashes))
        {
            return hashes;
        }

        return [(uint)Frosty.Sdk.Utils.Utils.HashString(value, true)];
    }

    private static void Add(Dictionary<uint, string> lookup, string value)
    {
        lookup[(uint)Frosty.Sdk.Utils.Utils.HashString(value, true)] = value;
    }

    private static void AddAlias(Dictionary<uint, string> lookup, string value, uint hash)
    {
        lookup[hash] = value;
    }

    private static void AddName(Dictionary<string, List<uint>> lookup, string value, uint? hash = null)
    {
        if (!lookup.TryGetValue(value, out List<uint>? hashes))
        {
            hashes = [];
            lookup[value] = hashes;
        }

        uint resolvedHash = hash ?? (uint)Frosty.Sdk.Utils.Utils.HashString(value, true);
        if (!hashes.Contains(resolvedHash))
        {
            hashes.Add(resolvedHash);
        }
    }
}
