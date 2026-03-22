using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace FrostyEditor.Managers;

public enum MeshPreviewView
{
    Perspective = 0,
    Front = 1,
    Back = 2,
    Left = 3,
    Right = 4,
    Top = 5,
    Bottom = 6
}

public readonly record struct MeshPreviewCameraState(float OrbitYaw, float OrbitPitch, float PanX, float PanY, float ZoomFactor)
{
    public static MeshPreviewCameraState Default => new(0.0f, 0.0f, 0.0f, 0.0f, 1.0f);
}

public readonly record struct MeshPreviewSectionState(bool IsVisible, bool IsHighlighted);
public sealed record MeshPreviewRenderResult(Bitmap? Bitmap, string Status);

public static class MeshPreviewRenderer
{
    private const int c_previewWidth = 720;
    private const int c_previewHeight = 520;
    private const float c_padding = 28.0f;
    private const float c_faceAreaEpsilon = 1e-12f;

    private static readonly Rgba32 s_backgroundTop = new(21, 24, 30, 255);
    private static readonly Rgba32 s_backgroundBottom = new(10, 12, 16, 255);
    private static readonly Rgba32 s_gridColor = new(48, 56, 67, 255);
    private static readonly Rgba32 s_wireColor = new(18, 20, 24, 255);
    private static readonly Rgba32 s_highlightColor = new(225, 90, 58, 255);
    private static readonly Rgba32[] s_palette =
    [
        new Rgba32(115, 162, 242, 255),
        new Rgba32(92, 201, 177, 255),
        new Rgba32(219, 190, 97, 255),
        new Rgba32(194, 128, 224, 255),
        new Rgba32(103, 185, 94, 255),
        new Rgba32(221, 132, 86, 255)
    ];

    private readonly record struct ScreenVertex(float X, float Y, float Depth);
    private readonly record struct RenderVertex(Vector3 Rotated, Vector3 Normal, float ProjectedX, float ProjectedY, float Depth);
    private sealed class PreparedPreviewScene
    {
        public required List<(MeshObjCodec.MeshDecodedSection Section, RenderVertex[] Vertices)> Sections { get; init; }
        public required float ProjectedCenterX { get; init; }
        public required float ProjectedCenterY { get; init; }
        public required float Scale { get; init; }
        public required float Radius { get; init; }
        public required int TotalSectionCount { get; init; }
        public required int VisibleSectionCount { get; init; }
    }

    public static MeshPreviewRenderResult CreatePreviewBitmap(
        MeshAssetLoadResult load,
        int? lodIndex,
        IReadOnlyDictionary<int, MeshPreviewSectionState>? sectionStates,
        int? selectedSectionIndex,
        MeshPreviewView previewView,
        MeshPreviewCameraState cameraState,
        MeshViewportRenderMode renderMode,
        MeshViewportChannelMask channelMask)
    {
        PreparedPreviewScene? scene = PreparePreviewScene(load, lodIndex, sectionStates, previewView, cameraState, out string unavailableStatus);
        if (scene is null)
        {
            return new MeshPreviewRenderResult(CreatePlaceholderBitmap("Preview unavailable"), unavailableStatus);
        }

        Rgba32[] colorBuffer = CreateBackground();
        float[] depthBuffer = CreateDepthBuffer();

        DrawGrid(colorBuffer, c_previewWidth, c_previewHeight, previewView);

        int renderedTriangles = 0;
        for (int sectionIndex = 0; sectionIndex < scene.Sections.Count; sectionIndex++)
        {
            (MeshObjCodec.MeshDecodedSection section, RenderVertex[] vertices) = scene.Sections[sectionIndex];
            MeshPreviewSectionState customState = default;
            bool hasCustomState = lodIndex.HasValue &&
                                  sectionStates is not null &&
                                  sectionStates.TryGetValue(section.Section.Index, out customState);
            bool isHighlighted = (selectedSectionIndex.HasValue &&
                                  section.Lod.Index == lodIndex &&
                                  section.Section.Index == selectedSectionIndex.Value) ||
                                 (hasCustomState && customState.IsHighlighted);
            Rgba32 baseColor = isHighlighted ? s_highlightColor : s_palette[sectionIndex % s_palette.Length];
            Vector4 baseColorVector = ApplyChannelMask(ToColorVector(baseColor), channelMask);

            for (int index = 0; index < section.Indices.Length; index += 3)
            {
                int indexA = section.Indices[index];
                int indexB = section.Indices[index + 1];
                int indexC = section.Indices[index + 2];
                RenderVertex a = vertices[indexA];
                RenderVertex b = vertices[indexB];
                RenderVertex c = vertices[indexC];

                ScreenVertex screenA = ToScreen(a, scene.ProjectedCenterX, scene.ProjectedCenterY, scene.Scale, cameraState);
                ScreenVertex screenB = ToScreen(b, scene.ProjectedCenterX, scene.ProjectedCenterY, scene.Scale, cameraState);
                ScreenVertex screenC = ToScreen(c, scene.ProjectedCenterX, scene.ProjectedCenterY, scene.Scale, cameraState);

                if (renderMode != MeshViewportRenderMode.Wireframe)
                {
                    Rgba32 fillColor = BuildFillColor(
                        section.Vertices[indexA],
                        section.Vertices[indexB],
                        section.Vertices[indexC],
                        a.Normal,
                        b.Normal,
                        c.Normal,
                        baseColorVector,
                        isHighlighted,
                        renderMode,
                        channelMask);
                    RasterizeTriangle(colorBuffer, depthBuffer, screenA, screenB, screenC, fillColor);
                }

                if (renderMode == MeshViewportRenderMode.Wireframe || isHighlighted)
                {
                    Rgba32 wireColor = renderMode == MeshViewportRenderMode.Wireframe
                        ? ToRgba(baseColorVector)
                        : s_wireColor;
                    DrawLine(colorBuffer, screenA, screenB, wireColor);
                    DrawLine(colorBuffer, screenB, screenC, wireColor);
                    DrawLine(colorBuffer, screenC, screenA, wireColor);
                }
                renderedTriangles++;
            }
        }

        Bitmap bitmap = CreateBitmap(colorBuffer);
        string status = BuildStatus(lodIndex, previewView, cameraState, scene.TotalSectionCount, scene.VisibleSectionCount, renderedTriangles, selectedSectionIndex, scene.Sections);
        return new MeshPreviewRenderResult(bitmap, status);
    }

    public static int? HitTestSection(
        MeshAssetLoadResult load,
        int? lodIndex,
        IReadOnlyDictionary<int, MeshPreviewSectionState>? sectionStates,
        MeshPreviewView previewView,
        MeshPreviewCameraState cameraState,
        float previewX,
        float previewY)
    {
        PreparedPreviewScene? scene = PreparePreviewScene(load, lodIndex, sectionStates, previewView, cameraState, out _);
        if (scene is null)
        {
            return null;
        }

        float closestDepth = float.PositiveInfinity;
        int? hitSectionIndex = null;
        float pixelX = previewX + 0.5f;
        float pixelY = previewY + 0.5f;

        foreach ((MeshObjCodec.MeshDecodedSection section, RenderVertex[] vertices) in scene.Sections)
        {
            for (int index = 0; index < section.Indices.Length; index += 3)
            {
                RenderVertex a = vertices[section.Indices[index]];
                RenderVertex b = vertices[section.Indices[index + 1]];
                RenderVertex c = vertices[section.Indices[index + 2]];

                ScreenVertex screenA = ToScreen(a, scene.ProjectedCenterX, scene.ProjectedCenterY, scene.Scale, cameraState);
                ScreenVertex screenB = ToScreen(b, scene.ProjectedCenterX, scene.ProjectedCenterY, scene.Scale, cameraState);
                ScreenVertex screenC = ToScreen(c, scene.ProjectedCenterX, scene.ProjectedCenterY, scene.Scale, cameraState);

                if (!TrySampleTriangle(pixelX, pixelY, screenA, screenB, screenC, out float depth))
                {
                    continue;
                }

                if (depth >= closestDepth)
                {
                    continue;
                }

                closestDepth = depth;
                hitSectionIndex = section.Section.Index;
            }
        }

        return hitSectionIndex;
    }

    private static string BuildStatus(
        int? lodIndex,
        MeshPreviewView previewView,
        MeshPreviewCameraState cameraState,
        int totalSectionCount,
        int visibleSectionCount,
        int triangleCount,
        int? selectedSectionIndex,
        IReadOnlyList<(MeshObjCodec.MeshDecodedSection Section, RenderVertex[] Vertices)> transformedSections)
    {
        string lodLabel = lodIndex.HasValue ? $"LOD {lodIndex.Value}" : "all LODs";
        string sectionLabel = "all sections";
        string zoomLabel = $"{cameraState.ZoomFactor * 100.0f:0}% zoom";
        string panLabel = $"pan {cameraState.PanX:0},{cameraState.PanY:0}";
        string orbitLabel = $", orbit {cameraState.OrbitYaw * 57.29578f:0}/{cameraState.OrbitPitch * 57.29578f:0}";

        if (selectedSectionIndex.HasValue)
        {
            MeshObjCodec.MeshDecodedSection? selectedSection = transformedSections
                .Select(item => item.Section)
                .FirstOrDefault(item => item.Section.Index == selectedSectionIndex.Value);

            if (selectedSection is not null)
            {
                sectionLabel = $"section {selectedSection.Section.Index} ({selectedSection.Section.Name})";
                return $"{lodLabel}, {sectionLabel}, {visibleSectionCount}/{totalSectionCount} sections visible, {triangleCount:N0} triangles, {previewView} view, {zoomLabel}, {panLabel}{orbitLabel}. {selectedSection.DecodeDiagnostics}";
            }
        }

        MeshObjCodec.MeshDecodedSection? firstSection = transformedSections.Select(item => item.Section).FirstOrDefault();
        string diagnosticsSuffix = firstSection is null ? string.Empty : $". {firstSection.DecodeDiagnostics}";
        return $"{lodLabel}, {sectionLabel}, {visibleSectionCount}/{totalSectionCount} sections visible, {triangleCount:N0} triangles, {previewView} view, {zoomLabel}, {panLabel}{orbitLabel}{diagnosticsSuffix}";
    }

    private static PreparedPreviewScene? PreparePreviewScene(
        MeshAssetLoadResult load,
        int? lodIndex,
        IReadOnlyDictionary<int, MeshPreviewSectionState>? sectionStates,
        MeshPreviewView previewView,
        MeshPreviewCameraState cameraState,
        out string unavailableStatus)
    {
        List<MeshObjCodec.MeshDecodedSection> sections = MeshObjCodec.DecodeSections(load, lodIndex);
        if (sections.Count == 0)
        {
            unavailableStatus = "This mesh does not expose triangle-list geometry that Frosty 2.0 can preview yet.";
            return null;
        }

        Vector3 minBounds = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 maxBounds = new(float.MinValue, float.MinValue, float.MinValue);
        List<MeshObjCodec.MeshDecodedSection> visibleSections = [];
        Dictionary<(int LodIndex, int SectionIndex), Matrix4x4[]?> palettes = [];

        foreach (MeshObjCodec.MeshDecodedSection section in sections)
        {
            if (!IsSectionVisible(lodIndex, sectionStates, section.Section.Index))
            {
                continue;
            }

            visibleSections.Add(section);
            Matrix4x4[]? transformPalette = MeshViewportTransformResolver.ResolvePalette(load, section);
            palettes[(section.Lod.Index, section.Section.Index)] = transformPalette;
            foreach (MeshObjCodec.MeshDecodedVertex vertex in section.Vertices)
            {
                MeshViewportTransformResolver.TransformVertex(section, vertex, transformPalette, out Vector3 position, out _);
                minBounds = Vector3.Min(minBounds, position);
                maxBounds = Vector3.Max(maxBounds, position);
            }
        }

        if (visibleSections.Count == 0 || minBounds.X == float.MaxValue)
        {
            unavailableStatus = "All sections are hidden in the current preview.";
            return null;
        }

        Vector3 center = (minBounds + maxBounds) * 0.5f;
        float radius = MathF.Max(0.001f, (maxBounds - minBounds).Length() * 0.5f);
        Matrix4x4 rotation = GetRotation(previewView, cameraState);

        List<(MeshObjCodec.MeshDecodedSection Section, RenderVertex[] Vertices)> transformedSections = [];
        float minProjectedX = float.MaxValue;
        float minProjectedY = float.MaxValue;
        float maxProjectedX = float.MinValue;
        float maxProjectedY = float.MinValue;

        foreach (MeshObjCodec.MeshDecodedSection section in visibleSections)
        {
            RenderVertex[] transformedVertices = new RenderVertex[section.Vertices.Length];
            palettes.TryGetValue((section.Lod.Index, section.Section.Index), out Matrix4x4[]? transformPalette);
            for (int i = 0; i < section.Vertices.Length; i++)
            {
                MeshViewportTransformResolver.TransformVertex(section, section.Vertices[i], transformPalette, out Vector3 worldPosition, out Vector3 worldNormal);
                Vector3 rotated = Vector3.Transform(worldPosition - center, rotation);
                Vector3 rotatedNormal = worldNormal.LengthSquared() > 0.000001f
                    ? Vector3.Normalize(Vector3.TransformNormal(worldNormal, rotation))
                    : Vector3.UnitZ;
                (float projectedX, float projectedY, float depth) = Project(rotated, radius, previewView);
                transformedVertices[i] = new RenderVertex(rotated, rotatedNormal, projectedX, projectedY, depth);
                minProjectedX = MathF.Min(minProjectedX, projectedX);
                minProjectedY = MathF.Min(minProjectedY, projectedY);
                maxProjectedX = MathF.Max(maxProjectedX, projectedX);
                maxProjectedY = MathF.Max(maxProjectedY, projectedY);
            }

            transformedSections.Add((section, transformedVertices));
        }

        float spanX = MathF.Max(0.001f, maxProjectedX - minProjectedX);
        float spanY = MathF.Max(0.001f, maxProjectedY - minProjectedY);
        float zoomFactor = Math.Clamp(cameraState.ZoomFactor, 0.2f, 8.0f);
        float scale = MathF.Min(
            (c_previewWidth - (c_padding * 2.0f)) / spanX,
            (c_previewHeight - (c_padding * 2.0f)) / spanY) * zoomFactor;

        unavailableStatus = string.Empty;
        return new PreparedPreviewScene
        {
            Sections = transformedSections,
            ProjectedCenterX = (minProjectedX + maxProjectedX) * 0.5f,
            ProjectedCenterY = (minProjectedY + maxProjectedY) * 0.5f,
            Scale = scale,
            Radius = radius,
            TotalSectionCount = sections.Count,
            VisibleSectionCount = visibleSections.Count
        };
    }

    private static Matrix4x4 GetRotation(MeshPreviewView previewView, MeshPreviewCameraState cameraState)
    {
        (float basePitch, float baseYaw) = previewView switch
        {
            MeshPreviewView.Front => (0.0f, 0.0f),
            MeshPreviewView.Back => (0.0f, MathF.PI),
            MeshPreviewView.Left => (0.0f, -MathF.PI * 0.5f),
            MeshPreviewView.Right => (0.0f, MathF.PI * 0.5f),
            MeshPreviewView.Top => (-MathF.PI * 0.5f, 0.0f),
            MeshPreviewView.Bottom => (MathF.PI * 0.5f, 0.0f),
            _ => (-0.38f, 0.72f)
        };

        float pitch = basePitch + cameraState.OrbitPitch;
        float yaw = baseYaw + cameraState.OrbitYaw;
        return Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationY(yaw);
    }

    private static (float ProjectedX, float ProjectedY, float Depth) Project(Vector3 rotated, float radius, MeshPreviewView previewView)
    {
        if (previewView == MeshPreviewView.Perspective)
        {
            float cameraDistance = MathF.Max(radius * 3.0f, 2.0f);
            float eyeDepth = cameraDistance - rotated.Z;
            eyeDepth = MathF.Max(0.01f, eyeDepth);
            return (rotated.X / eyeDepth, rotated.Y / eyeDepth, eyeDepth);
        }

        return (rotated.X, rotated.Y, -rotated.Z);
    }

    private static ScreenVertex ToScreen(RenderVertex vertex, float projectedCenterX, float projectedCenterY, float scale, MeshPreviewCameraState cameraState)
    {
        float x = (c_previewWidth * 0.5f) + ((vertex.ProjectedX - projectedCenterX) * scale) + cameraState.PanX;
        float y = (c_previewHeight * 0.5f) - ((vertex.ProjectedY - projectedCenterY) * scale) + cameraState.PanY;
        return new ScreenVertex(x, y, vertex.Depth);
    }

    private static Rgba32 Shade(Rgba32 baseColor, Vector3 surfaceNormal, bool isHighlighted)
    {
        Vector3 lightDirection = Vector3.Normalize(new Vector3(0.25f, 0.6f, 1.0f));
        Vector3 normal = surfaceNormal.LengthSquared() > 0.000001f ? Vector3.Normalize(surfaceNormal) : Vector3.UnitZ;
        float diffuse = MathF.Max(0.0f, Vector3.Dot(normal, lightDirection));
        float intensity = isHighlighted ? 0.42f + (diffuse * 0.58f) : 0.28f + (diffuse * 0.52f);

        return new Rgba32(
            (byte)Math.Clamp(baseColor.R * intensity, 0, 255),
            (byte)Math.Clamp(baseColor.G * intensity, 0, 255),
            (byte)Math.Clamp(baseColor.B * intensity, 0, 255),
            255);
    }

    private static Rgba32[] CreateBackground()
    {
        Rgba32[] pixels = new Rgba32[c_previewWidth * c_previewHeight];
        for (int y = 0; y < c_previewHeight; y++)
        {
            float t = y / (float)Math.Max(1, c_previewHeight - 1);
            byte red = (byte)Lerp(s_backgroundTop.R, s_backgroundBottom.R, t);
            byte green = (byte)Lerp(s_backgroundTop.G, s_backgroundBottom.G, t);
            byte blue = (byte)Lerp(s_backgroundTop.B, s_backgroundBottom.B, t);

            for (int x = 0; x < c_previewWidth; x++)
            {
                pixels[(y * c_previewWidth) + x] = new Rgba32(red, green, blue, 255);
            }
        }

        return pixels;
    }

    private static float[] CreateDepthBuffer()
    {
        float[] depthBuffer = new float[c_previewWidth * c_previewHeight];
        Array.Fill(depthBuffer, float.PositiveInfinity);
        return depthBuffer;
    }

    private static void DrawGrid(Rgba32[] colorBuffer, int width, int height, MeshPreviewView previewView)
    {
        if (previewView != MeshPreviewView.Perspective)
        {
            int centerX = width / 2;
            int centerY = height / 2;
            DrawLine(colorBuffer, new ScreenVertex(centerX, c_padding, 0), new ScreenVertex(centerX, height - c_padding, 0), s_gridColor);
            DrawLine(colorBuffer, new ScreenVertex(c_padding, centerY, 0), new ScreenVertex(width - c_padding, centerY, 0), s_gridColor);
            return;
        }

        float horizon = height * 0.66f;
        for (int i = -4; i <= 4; i++)
        {
            float offset = i * 48.0f;
            DrawLine(
                colorBuffer,
                new ScreenVertex((width * 0.5f) + offset, horizon, 0),
                new ScreenVertex((width * 0.5f) + (offset * 1.7f), height - c_padding, 0),
                s_gridColor);
        }

        for (int i = 1; i <= 5; i++)
        {
            float y = Lerp(horizon, height - c_padding, i / 5.0f);
            DrawLine(colorBuffer, new ScreenVertex(c_padding, y, 0), new ScreenVertex(width - c_padding, y, 0), s_gridColor);
        }
    }

    private static void RasterizeTriangle(
        Rgba32[] colorBuffer,
        float[] depthBuffer,
        ScreenVertex a,
        ScreenVertex b,
        ScreenVertex c,
        Rgba32 fillColor)
    {
        float area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (MathF.Abs(area) < 0.0001f)
        {
            return;
        }

        int minX = ClampToPixel(MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), c_previewWidth);
        int maxX = ClampToPixel(MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), c_previewWidth);
        int minY = ClampToPixel(MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), c_previewHeight);
        int maxY = ClampToPixel(MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), c_previewHeight);

        for (int y = minY; y <= maxY; y++)
        {
            float pixelY = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                if (!TrySampleTriangle(x + 0.5f, pixelY, a, b, c, out float depth))
                {
                    continue;
                }

                int bufferIndex = (y * c_previewWidth) + x;
                if (depth >= depthBuffer[bufferIndex])
                {
                    continue;
                }

                depthBuffer[bufferIndex] = depth;
                colorBuffer[bufferIndex] = fillColor;
            }
        }
    }

    private static void DrawLine(Rgba32[] colorBuffer, ScreenVertex start, ScreenVertex end, Rgba32 color)
    {
        float deltaX = end.X - start.X;
        float deltaY = end.Y - start.Y;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(deltaX), MathF.Abs(deltaY))));

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = (int)MathF.Round(Lerp(start.X, end.X, t));
            int y = (int)MathF.Round(Lerp(start.Y, end.Y, t));
            if (x < 0 || x >= c_previewWidth || y < 0 || y >= c_previewHeight)
            {
                continue;
            }

            colorBuffer[(y * c_previewWidth) + x] = color;
        }
    }

    private static Bitmap CreateBitmap(Rgba32[] pixels)
    {
        using Image<Rgba32> image = new(c_previewWidth, c_previewHeight);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                pixels.AsSpan(y * c_previewWidth, c_previewWidth).CopyTo(accessor.GetRowSpan(y));
            }
        });

        using MemoryStream stream = new();
        image.Save(stream, new PngEncoder());
        stream.Position = 0;
        return new Bitmap(stream);
    }

    private static Bitmap CreatePlaceholderBitmap(string label)
    {
        Rgba32[] pixels = CreateBackground();
        int y = c_previewHeight / 2;
        for (int x = 80; x < c_previewWidth - 80; x++)
        {
            pixels[(y * c_previewWidth) + x] = new Rgba32(80, 92, 108, 255);
        }

        return CreateBitmap(pixels);
    }

    private static bool IsSectionVisible(
        int? lodIndex,
        IReadOnlyDictionary<int, MeshPreviewSectionState>? sectionStates,
        int sectionIndex)
    {
        return !lodIndex.HasValue ||
               sectionStates is null ||
               !sectionStates.TryGetValue(sectionIndex, out MeshPreviewSectionState state) ||
               state.IsVisible;
    }

    private static bool TrySampleTriangle(
        float pixelX,
        float pixelY,
        ScreenVertex a,
        ScreenVertex b,
        ScreenVertex c,
        out float depth)
    {
        float area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (MathF.Abs(area) < 0.0001f)
        {
            depth = float.PositiveInfinity;
            return false;
        }

        float w0 = Edge(b.X, b.Y, c.X, c.Y, pixelX, pixelY) / area;
        float w1 = Edge(c.X, c.Y, a.X, a.Y, pixelX, pixelY) / area;
        float w2 = Edge(a.X, a.Y, b.X, b.Y, pixelX, pixelY) / area;
        if (w0 < 0.0f || w1 < 0.0f || w2 < 0.0f)
        {
            depth = float.PositiveInfinity;
            return false;
        }

        depth = (w0 * a.Depth) + (w1 * b.Depth) + (w2 * c.Depth);
        return true;
    }

    private static int ClampToPixel(float value, int dimension)
    {
        return (int)Math.Clamp(value, 0, dimension - 1);
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py)
    {
        return ((px - ax) * (by - ay)) - ((py - ay) * (bx - ax));
    }

    private static float Lerp(float start, float end, float amount)
    {
        return start + ((end - start) * amount);
    }

    private static Rgba32 BuildFillColor(
        MeshObjCodec.MeshDecodedVertex vertexA,
        MeshObjCodec.MeshDecodedVertex vertexB,
        MeshObjCodec.MeshDecodedVertex vertexC,
        Vector3 normalA,
        Vector3 normalB,
        Vector3 normalC,
        Vector4 baseColor,
        bool isHighlighted,
        MeshViewportRenderMode renderMode,
        MeshViewportChannelMask channelMask)
    {
        if (renderMode == MeshViewportRenderMode.Lit)
        {
            Vector3 averagedNormal = normalA + normalB + normalC;
            if (averagedNormal.LengthSquared() < c_faceAreaEpsilon)
            {
                averagedNormal = Vector3.UnitZ;
            }

            return Shade(ToRgba(baseColor), averagedNormal, isHighlighted);
        }

        if (renderMode == MeshViewportRenderMode.Base)
        {
            return ToRgba(baseColor);
        }

        Vector4 colorA = BuildDisplayColor(vertexA, baseColor, renderMode, channelMask);
        Vector4 colorB = BuildDisplayColor(vertexB, baseColor, renderMode, channelMask);
        Vector4 colorC = BuildDisplayColor(vertexC, baseColor, renderMode, channelMask);
        Vector4 averageColor = (colorA + colorB + colorC) / 3.0f;
        return ToRgba(averageColor);
    }

    private static Vector4 BuildDisplayColor(
        MeshObjCodec.MeshDecodedVertex vertex,
        Vector4 baseColor,
        MeshViewportRenderMode renderMode,
        MeshViewportChannelMask channelMask)
    {
        Vector4 displayColor = renderMode switch
        {
            MeshViewportRenderMode.Lit => baseColor,
            MeshViewportRenderMode.Base => baseColor,
            MeshViewportRenderMode.Wireframe => baseColor,
            MeshViewportRenderMode.Normals => BuildNormalColor(vertex),
            MeshViewportRenderMode.TexCoord0 => BuildTexCoordColor(vertex),
            MeshViewportRenderMode.Color0 => BuildVertexColor(vertex, baseColor),
            MeshViewportRenderMode.BoneWeights => BuildBoneWeightColor(vertex, baseColor),
            _ => baseColor
        };

        return ApplyChannelMask(displayColor, channelMask);
    }

    private static Vector4 BuildNormalColor(MeshObjCodec.MeshDecodedVertex vertex)
    {
        Vector3 normal = vertex.Normal.LengthSquared() > 0.000001f
            ? Vector3.Normalize(vertex.Normal)
            : Vector3.UnitZ;
        return new Vector4(
            (normal.X * 0.5f) + 0.5f,
            (normal.Y * 0.5f) + 0.5f,
            (normal.Z * 0.5f) + 0.5f,
            1.0f);
    }

    private static Vector4 BuildTexCoordColor(MeshObjCodec.MeshDecodedVertex vertex)
    {
        float u = Repeat01(vertex.TexCoords[0].X);
        float v = Repeat01(vertex.TexCoords[0].Y);
        return new Vector4(u, v, 1.0f - Repeat01((u + v) * 0.5f), 1.0f);
    }

    private static Vector4 BuildVertexColor(MeshObjCodec.MeshDecodedVertex vertex, Vector4 fallback)
    {
        Vector4 color = vertex.Colors[0];
        if ((MathF.Abs(color.X) + MathF.Abs(color.Y) + MathF.Abs(color.Z) + MathF.Abs(color.W)) < 0.0001f)
        {
            return fallback;
        }

        return new Vector4(
            Math.Clamp(color.X, 0.0f, 1.0f),
            Math.Clamp(color.Y, 0.0f, 1.0f),
            Math.Clamp(color.Z, 0.0f, 1.0f),
            Math.Clamp(color.W, 0.0f, 1.0f));
    }

    private static Vector4 BuildBoneWeightColor(MeshObjCodec.MeshDecodedVertex vertex, Vector4 fallback)
    {
        float w0 = Math.Max(0.0f, vertex.BoneWeights[0]);
        float w1 = Math.Max(0.0f, vertex.BoneWeights[1]);
        float w2 = Math.Max(0.0f, vertex.BoneWeights[2]);
        float w3 = Math.Max(0.0f, vertex.BoneWeights[3]);
        float total = w0 + w1 + w2 + w3;
        if (total <= 0.0001f)
        {
            return fallback;
        }

        float scale = 1.0f / total;
        return new Vector4(w0 * scale, w1 * scale, w2 * scale, 1.0f);
    }

    private static Vector4 ApplyChannelMask(Vector4 color, MeshViewportChannelMask channelMask)
    {
        bool showRed = (channelMask & MeshViewportChannelMask.Red) != 0;
        bool showGreen = (channelMask & MeshViewportChannelMask.Green) != 0;
        bool showBlue = (channelMask & MeshViewportChannelMask.Blue) != 0;
        bool showAlpha = (channelMask & MeshViewportChannelMask.Alpha) != 0;

        if (!showRed && !showGreen && !showBlue)
        {
            if (showAlpha)
            {
                float alpha = Math.Clamp(color.W, 0.0f, 1.0f);
                return new Vector4(alpha, alpha, alpha, 1.0f);
            }

            return new Vector4(color.X, color.Y, color.Z, 1.0f);
        }

        return new Vector4(
            showRed ? Math.Clamp(color.X, 0.0f, 1.0f) : 0.0f,
            showGreen ? Math.Clamp(color.Y, 0.0f, 1.0f) : 0.0f,
            showBlue ? Math.Clamp(color.Z, 0.0f, 1.0f) : 0.0f,
            showAlpha ? Math.Clamp(color.W, 0.0f, 1.0f) : 1.0f);
    }

    private static float Repeat01(float value)
    {
        float wrapped = value - MathF.Floor(value);
        return wrapped < 0.0f ? wrapped + 1.0f : wrapped;
    }

    private static Vector4 ToColorVector(Rgba32 color)
    {
        return new Vector4(
            color.R / 255.0f,
            color.G / 255.0f,
            color.B / 255.0f,
            color.A / 255.0f);
    }

    private static Rgba32 ToRgba(Vector4 color)
    {
        return new Rgba32(
            (byte)Math.Clamp(MathF.Round(Math.Clamp(color.X, 0.0f, 1.0f) * 255.0f), byte.MinValue, byte.MaxValue),
            (byte)Math.Clamp(MathF.Round(Math.Clamp(color.Y, 0.0f, 1.0f) * 255.0f), byte.MinValue, byte.MaxValue),
            (byte)Math.Clamp(MathF.Round(Math.Clamp(color.Z, 0.0f, 1.0f) * 255.0f), byte.MinValue, byte.MaxValue),
            (byte)Math.Clamp(MathF.Round(Math.Clamp(color.W, 0.0f, 1.0f) * 255.0f), byte.MinValue, byte.MaxValue));
    }
}
