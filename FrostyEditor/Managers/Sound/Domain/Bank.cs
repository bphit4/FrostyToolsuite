using System.Collections.Generic;
using System.Linq;
using Sdk.Hash;

namespace Editor.Lib.Exporters.Sounds;

public record Bank(uint Key, uint ProjectKey, List<DataSet> DataSets, byte[] DataBlock)
{
	public DataSet Get(string name)
	{
		uint nameHash = Djb2Hash.HashString32(name);
		return DataSets.FirstOrDefault((DataSet ds) => ds.Id == nameHash);
	}
}
