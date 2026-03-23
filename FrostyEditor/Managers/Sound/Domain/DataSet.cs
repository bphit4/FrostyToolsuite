using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Sdk.Hash;

namespace Editor.Lib.Exporters.Sounds;

public record DataSet
{
	public uint Id { get; init; }

	public uint SampleGroupId { get; init; }

	public uint DataOffset { get; init; }

	public Field[] Fields { get; init; }

	public Field[] IndexColumns { get; init; }

	public DataSetIndex[] Indexes { get; init; }

	public int NumElems
	{
		get
		{
			return numElems;
		}
		set
		{
			numElems = value;
		}
	}

	private int numElems;

	public DataSet(uint Id, uint SampleGroupId, uint DataOffset, int NumElems, Field[] Fields, Field[] IndexColumns, DataSetIndex[] Indexes)
	{
		this.Id = Id;
		this.SampleGroupId = SampleGroupId;
		this.DataOffset = DataOffset;
		this.Fields = Fields;
		this.IndexColumns = IndexColumns;
		this.Indexes = Indexes;
		numElems = NumElems;
	}

	public Field Get(uint key)
	{
		return Fields.FirstOrDefault((Field f) => f.Id == key) ?? IndexColumns.FirstOrDefault((Field f) => f.Id == key);
	}

	public Field Get(string name)
	{
		uint key = Djb2Hash.HashString32(name);
		return Get(key);
	}

	[CompilerGenerated]
	protected virtual bool PrintMembers(StringBuilder builder)
	{
		RuntimeHelpers.EnsureSufficientExecutionStack();
		builder.Append("Id = ");
		builder.Append(Id.ToString());
		builder.Append(", SampleGroupId = ");
		builder.Append(SampleGroupId.ToString());
		builder.Append(", DataOffset = ");
		builder.Append(DataOffset.ToString());
		builder.Append(", Fields = ");
		builder.Append(Fields);
		builder.Append(", IndexColumns = ");
		builder.Append(IndexColumns);
		builder.Append(", Indexes = ");
		builder.Append(Indexes);
		builder.Append(", NumElems = ");
		builder.Append(NumElems.ToString());
		return true;
	}

	[CompilerGenerated]
	public void Deconstruct(out uint Id, out uint SampleGroupId, out uint DataOffset, out int NumElems, out Field[] Fields, out Field[] IndexColumns, out DataSetIndex[] Indexes)
	{
		Id = this.Id;
		SampleGroupId = this.SampleGroupId;
		DataOffset = this.DataOffset;
		NumElems = this.NumElems;
		Fields = this.Fields;
		IndexColumns = this.IndexColumns;
		Indexes = this.Indexes;
	}
}
