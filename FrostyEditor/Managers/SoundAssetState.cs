using System;
using System.Collections.Generic;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.Models.Audio;

namespace FrostyEditor.Managers;

public sealed record SoundAssetState(EbxAssetEntry Entry, EbxPartition Partition, object RootObject, IReadOnlyList<SoundChunkBinding> Chunks, SoundAssetKind Kind, NewWaveBank? ParsedBank, Guid? BankContainerChunkId, Endian? BankEndian, ResAssetEntry? ResEntry, byte[]? ResMeta);
