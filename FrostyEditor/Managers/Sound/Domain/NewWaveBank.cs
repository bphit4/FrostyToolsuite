using System;
using System.Collections.Generic;
using System.Linq;
using Sdk.Hash;

namespace Editor.Lib.Exporters.Sounds;

public record NewWaveBank(uint Key, uint ProjectKey, List<Variation> Variations, List<DataSet> AllDataSets, List<DataSet> UnknownDataSets)
{
	public Guid EbxInstanceGuid { get; set; }

	public uint SelectionDatasetSampleGroupId { get; set; }

	public int[] SelectionParameterIds { get; set; } = Array.Empty<int>();

	public bool SelectionParametersOnly { get; set; }

	public uint TrailerBankKey { get; set; }

	public DataSet GetDataSet(string name)
	{
		uint nameHash = Djb2Hash.HashString32(name);
		return AllDataSets.FirstOrDefault((DataSet ds) => ds.Id == nameHash);
	}
}
