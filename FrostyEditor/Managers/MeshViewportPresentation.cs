using System;

namespace FrostyEditor.Managers;

public enum MeshViewportRenderMode
{
    Lit = 0,
    Base = 1,
    Wireframe = 2,
    Normals = 3,
    TexCoord0 = 4,
    Color0 = 5,
    BoneWeights = 6
}

[Flags]
public enum MeshViewportChannelMask
{
    None = 0,
    Red = 1 << 0,
    Green = 1 << 1,
    Blue = 1 << 2,
    Alpha = 1 << 3,
    Rgb = Red | Green | Blue,
    Rgba = Rgb | Alpha
}
