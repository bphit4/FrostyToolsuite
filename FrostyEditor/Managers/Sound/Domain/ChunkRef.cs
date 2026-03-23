using System;

namespace Editor.Lib.Exporters.Sounds;

public record ChunkRef
{
	public ulong Index { get; set; }

	public Guid ChunkId { get; set; }

	public long ChunkSize { get; set; }

	public ChunkRef()
	{
	}

	public ChunkRef(ulong index, Guid chunkId, long chunkSize)
		: this()
	{
		Index = index;
		ChunkId = chunkId;
		ChunkSize = chunkSize;
	}
}
