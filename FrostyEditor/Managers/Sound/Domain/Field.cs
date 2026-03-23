using System.Collections.ObjectModel;
using Sdk;

namespace Editor.Lib.Exporters.Sounds;

public record Field(Endian Endian, uint Id, FieldType DataType, ColumnFormat OriginalFormat, uint TableOffset, uint NextReferenceOffset, ObservableCollection<object> Values);
