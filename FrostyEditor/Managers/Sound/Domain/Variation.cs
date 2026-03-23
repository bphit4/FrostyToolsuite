using System.Collections.Generic;

namespace Editor.Lib.Exporters.Sounds;

public record Variation
{
	public int Index { get; set; }

	public uint VariationId { get; set; }

	public ChunkRef ChunkRef { get; set; }

	public List<Segment> Segments { get; set; }

	public Dictionary<uint, object> SelectionColumnValues { get; set; }

	public Variation()
	{
	}

	public Variation(int index, uint variationId, ChunkRef chunkRef, List<Segment> segments, Dictionary<uint, object> selectionColumnValues)
		: this()
	{
		Index = index;
		VariationId = variationId;
		ChunkRef = chunkRef;
		Segments = segments;
		SelectionColumnValues = selectionColumnValues;
	}
}
