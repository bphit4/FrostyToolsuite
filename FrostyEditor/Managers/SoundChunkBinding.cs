using System;
using Frosty.Sdk.Managers.Entries;

namespace FrostyEditor.Managers;

public sealed record SoundChunkBinding(int Index, Guid ChunkId, long ChunkSize, string Name, object? SourceObject, ChunkAssetEntry? ChunkEntry);
