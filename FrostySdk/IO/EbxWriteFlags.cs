using System;

namespace Frosty.Sdk.IO;

[Flags]
public enum EbxWriteFlags
{
    None = 0,
    DoNotSort = 1 << 0
}
