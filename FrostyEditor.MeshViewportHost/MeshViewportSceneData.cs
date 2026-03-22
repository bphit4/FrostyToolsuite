using System.Collections.Generic;

namespace FrostyEditor.MeshViewportHost;

public sealed class MeshViewportSceneData
{
    public string Name { get; init; } = string.Empty;
    public string ObjPath { get; init; } = string.Empty;
    public int CurrentLod { get; init; }
    public bool TexturesEnabled { get; init; } = true;
    public bool Wireframe { get; init; }
    public MeshViewportViewPreset ViewPreset { get; init; } = MeshViewportViewPreset.Perspective;
    public IReadOnlyList<MeshViewportLodData> Lods { get; init; } = [];
}

public sealed class MeshViewportLodData
{
    public int Index { get; init; }
    public IReadOnlyList<MeshViewportSectionData> Sections { get; init; } = [];
}

public sealed class MeshViewportSectionData
{
    public string ObjectName { get; init; } = string.Empty;
    public int SectionIndex { get; init; }
    public int MaterialId { get; init; }
    public bool Visible { get; init; } = true;
    public byte[]? DiffuseTexturePng { get; init; }
    public byte[]? NormalTexturePng { get; init; }
}

public enum MeshViewportViewPreset
{
    Perspective = 0,
    Front = 1,
    Back = 2,
    Left = 3,
    Right = 4,
    Top = 5,
    Bottom = 6
}
