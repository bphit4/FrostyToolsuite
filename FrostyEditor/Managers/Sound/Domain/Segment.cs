using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace Editor.Lib.Exporters.Sounds;

public record Segment : INotifyPropertyChanged
{
	public int Index
	{
		get
		{
			return index;
		}
		set
		{
			if (index != value)
			{
				index = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Index"));
			}
		}
	}

	public uint SamplesOffset
	{
		get
		{
			return samplesOffset;
		}
		set
		{
			if (samplesOffset != value)
			{
				samplesOffset = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SamplesOffset"));
			}
		}
	}

	public uint SeekTableOffset
	{
		get
		{
			return seekTableOffset;
		}
		set
		{
			if (seekTableOffset != value)
			{
				seekTableOffset = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SeekTableOffset"));
			}
		}
	}

	public float SegmentLength
	{
		get
		{
			return segmentLength;
		}
		set
		{
			if (segmentLength != value)
			{
				segmentLength = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SegmentLength"));
			}
		}
	}

	private int index;

	private uint samplesOffset;

	private uint seekTableOffset;

	private float segmentLength;

	public event PropertyChangedEventHandler PropertyChanged;

	public Segment(int index, uint samplesOffset, uint seekTableOffset, float segmentLength)
	{
		Index = index;
		SamplesOffset = samplesOffset;
		SeekTableOffset = seekTableOffset;
		SegmentLength = segmentLength;
	}

	[CompilerGenerated]
	protected virtual bool PrintMembers(StringBuilder builder)
	{
		RuntimeHelpers.EnsureSufficientExecutionStack();
		builder.Append("Index = ");
		builder.Append(Index.ToString());
		builder.Append(", SamplesOffset = ");
		builder.Append(SamplesOffset.ToString());
		builder.Append(", SeekTableOffset = ");
		builder.Append(SeekTableOffset.ToString());
		builder.Append(", SegmentLength = ");
		builder.Append(SegmentLength.ToString());
		return true;
	}
}
