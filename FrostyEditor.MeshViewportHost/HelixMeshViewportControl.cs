using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.Wpf.SharpDX.Assimp;
using HelixToolkit.Wpf.SharpDX.Model;
using HelixToolkit.Wpf.SharpDX.Model.Scene;
using Color4 = SharpDX.Color4;
using MediaColor = System.Windows.Media.Color;
using MediaPoint3D = System.Windows.Media.Media3D.Point3D;
using MediaRect3D = System.Windows.Media.Media3D.Rect3D;
using MediaVector3D = System.Windows.Media.Media3D.Vector3D;

namespace FrostyEditor.MeshViewportHost;

public sealed class HelixMeshViewportControl : UserControl, IDisposable
{
    private sealed class SectionVisualState
    {
        public required string MaterialKey { get; set; }
        public required bool Visible { get; set; }
        public required MaterialCore Material { get; set; }
        public required List<MeshNode> Nodes { get; init; }
    }

    private sealed class MaterialCacheEntry
    {
        public required MaterialCore Material { get; init; }
    }

    private readonly DefaultEffectsManager m_effectsManager = new();
    private readonly SceneNodeGroupModel3D m_groupModel = new();
    private readonly List<List<SectionVisualState>> m_lodSections = [];
    private readonly Dictionary<string, MaterialCacheEntry> m_materialCache = [];
    private readonly Dictionary<string, List<MeshNode>> m_nodesByObjectName = [];
    private readonly List<List<MeshNode>> m_importedSectionGroups = [];
    private readonly HelixToolkit.Wpf.SharpDX.OrthographicCamera m_orthographicCamera;
    private readonly HelixToolkit.Wpf.SharpDX.PerspectiveCamera m_perspectiveCamera;
    private readonly Viewport3DX m_viewport;
    private readonly DirectionalLight3D m_mainDirectionalLight;
    private readonly MaterialCore m_fallbackMaterial;
    private MeshViewportSceneData? m_sceneData;
    private MeshViewportViewPreset m_currentPreset = MeshViewportViewPreset.Perspective;
    private string? m_loadedObjPath;
    private MediaRect3D m_sceneBounds = MediaRect3D.Empty;
    private bool m_pendingSceneFit;
    private bool m_disposed;

    public HelixMeshViewportControl()
    {
        m_orthographicCamera = new HelixToolkit.Wpf.SharpDX.OrthographicCamera
        {
            LookDirection = new MediaVector3D(0.0, 0.0, -5.0),
            Position = new MediaPoint3D(0.0, 0.0, 1.0),
            FarPlaneDistance = 200.0,
            NearPlaneDistance = 0.001,
            Width = 4.0
        };
        m_perspectiveCamera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
        {
            LookDirection = new MediaVector3D(3.05, 1.85, -3.5),
            Position = new MediaPoint3D(-3.05, -1.85, 3.5),
            FarPlaneDistance = 200.0,
            NearPlaneDistance = 0.001,
            FieldOfView = 45.0
        };

        m_viewport = new Viewport3DX
        {
            ShowFrameRate = false,
            EnableD2DRendering = true,
            EnableSwapChainRendering = true,
            ShowViewCube = false,
            ShowCameraTarget = false,
            Orthographic = false,
            CameraMode = CameraMode.Inspect,
            FixedRotationPointEnabled = true,
            ZoomAroundMouseDownPoint = true,
            EffectsManager = m_effectsManager,
            Camera = m_perspectiveCamera,
            BackgroundColor = (MediaColor)ColorConverter.ConvertFromString("#161A1F")!
        };

        m_viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.RightClick)));
        m_viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.LeftClick)));
        m_viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Zoom, new MouseGesture(MouseAction.MiddleClick)));
        m_viewport.InputBindings.Add(new KeyBinding(ViewportCommands.ZoomExtents, new KeyGesture(Key.Z, ModifierKeys.Control)));

        m_mainDirectionalLight = new DirectionalLight3D { Direction = new MediaVector3D(0.0, 0.0, -1.0), Color = (MediaColor)ColorConverter.ConvertFromString("#7C7C7C")! };
        m_viewport.Items.Add(m_mainDirectionalLight);
        m_viewport.Items.Add(new DirectionalLight3D { Direction = new MediaVector3D(-1.0, -1.0, -1.0), Color = (MediaColor)ColorConverter.ConvertFromString("#A4A4A4")! });
        m_viewport.Items.Add(new DirectionalLight3D { Direction = new MediaVector3D(1.0, -1.0, -0.1), Color = (MediaColor)ColorConverter.ConvertFromString("#686868")! });
        m_viewport.Items.Add(new DirectionalLight3D { Direction = new MediaVector3D(0.1, 1.0, -1.0), Color = (MediaColor)ColorConverter.ConvertFromString("#3C3C3C")! });
        m_viewport.Items.Add(new DirectionalLight3D { Direction = new MediaVector3D(0.1, 0.1, 1.0), Color = (MediaColor)ColorConverter.ConvertFromString("#323232")! });
        m_viewport.Items.Add(new AmbientLight3D { Color = (MediaColor)ColorConverter.ConvertFromString("#1D1D1D")! });
        m_viewport.Items.Add(new Element3DPresenter { Content = m_groupModel });
        m_fallbackMaterial = CreateFallbackMaterial();

        Content = new Grid
        {
            Background = Brushes.Black,
            Children = { m_viewport }
        };

        SizeChanged += OnHostSizeChanged;
    }

    public void LoadScene(MeshViewportSceneData scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (string.IsNullOrWhiteSpace(scene.ObjPath) || !File.Exists(scene.ObjPath))
        {
            return;
        }

        if (CanReuseLoadedScene(scene))
        {
            m_sceneData = scene;
            UpdateSectionState(scene);
            ApplySceneSettings(scene.CurrentLod, scene.TexturesEnabled, scene.Wireframe);
            if (scene.ViewPreset != m_currentPreset)
            {
                ApplyView(scene.ViewPreset);
            }
            return;
        }

        m_sceneData = scene;
        m_loadedObjPath = scene.ObjPath;
        m_groupModel.Clear(true);
        m_lodSections.Clear();
        m_nodesByObjectName.Clear();
        m_importedSectionGroups.Clear();
        m_materialCache.Clear();
        m_sceneBounds = MediaRect3D.Empty;

        Importer importer = new();
        importer.Configuration.AssimpPostProcessSteps &= ~Assimp.PostProcessSteps.FindDegenerates;
        importer.Configuration.CullMode = SharpDX.Direct3D11.CullMode.None;
        using FileStream stream = new(scene.ObjPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ErrorCode errorCode = importer.Load(stream, scene.ObjPath, Path.GetExtension(scene.ObjPath), out HelixToolkitScene importedScene);
        if (importedScene is null || !errorCode.HasFlag(ErrorCode.Succeed))
        {
            return;
        }

        BuildImportedNodeLookup(importedScene);

        int importedSectionIndex = 0;
        foreach (MeshViewportLodData lod in scene.Lods)
        {
            List<SectionVisualState> lodSections = [];
            foreach (MeshViewportSectionData section in lod.Sections)
            {
                if (!TryGetImportedSectionNodes(section, importedSectionIndex, out List<MeshNode> sectionNodes))
                {
                    importedSectionIndex++;
                    continue;
                }

                MaterialCore material = CreateMaterial(section);
                string materialKey = BuildMaterialKey(section);
                ApplyMaterialToNodes(sectionNodes, material);

                lodSections.Add(new SectionVisualState
                {
                    MaterialKey = materialKey,
                    Visible = section.Visible,
                    Material = material,
                    Nodes = sectionNodes
                });

                importedSectionIndex++;
            }

            m_lodSections.Add(lodSections);
        }

        m_groupModel.AddNode(importedScene.Root);
        ApplySceneSettings(scene.CurrentLod, scene.TexturesEnabled, scene.Wireframe);
        ApplyView(scene.ViewPreset);
    }

    public void ApplySceneSettings(int currentLod, bool texturesEnabled, bool wireframe)
    {
        SharpDX.Direct3D11.FillMode nextFillMode = wireframe
            ? SharpDX.Direct3D11.FillMode.Wireframe
            : SharpDX.Direct3D11.FillMode.Solid;

        for (int lodIndex = 0; lodIndex < m_lodSections.Count; lodIndex++)
        {
            bool isVisibleLod = lodIndex == currentLod;
            foreach (SectionVisualState section in m_lodSections[lodIndex])
            {
                MaterialCore nextMaterial = texturesEnabled ? section.Material : m_fallbackMaterial;
                foreach (MeshNode meshNode in section.Nodes)
                {
                    bool nextVisible = isVisibleLod && section.Visible;
                    if (meshNode.Visible != nextVisible)
                    {
                        meshNode.Visible = nextVisible;
                    }

                    if (meshNode.FillMode != nextFillMode)
                    {
                        meshNode.FillMode = nextFillMode;
                    }

                    if (!ReferenceEquals(meshNode.Material, nextMaterial))
                    {
                        meshNode.Material = nextMaterial;
                    }
                }
            }
        }

        m_sceneBounds = MediaRect3D.Empty;
    }

    public void ApplyView(MeshViewportViewPreset preset)
    {
        m_currentPreset = preset;
        SwitchCamera(preset);

        MediaVector3D direction = GetPresetDirection(preset);
        MediaVector3D up = GetPresetUpDirection(preset);

        if (m_viewport.Camera is HelixToolkit.Wpf.SharpDX.Camera camera)
        {
            direction.Normalize();
            double distance = GetDefaultCameraDistance();
            MediaVector3D lookDirection = direction * distance;
            MediaPoint3D target = GetSceneCenter() ?? new MediaPoint3D(0.0, 0.0, 0.0);
            camera.LookDirection = lookDirection;
            camera.Position = target - lookDirection;
            camera.UpDirection = up;
        }

        EnsureCurrentProjectionFitsBounds();
    }

    public void ResetView()
    {
        ApplyView(m_currentPreset);
    }

    public void Dispose()
    {
        if (m_disposed)
        {
            return;
        }

        m_disposed = true;
        SizeChanged -= OnHostSizeChanged;
        m_groupModel.Clear(true);
        m_viewport.Items.Clear();
        Content = null;
        m_materialCache.Clear();
        m_nodesByObjectName.Clear();
        m_importedSectionGroups.Clear();
        m_effectsManager.Dispose();
        m_viewport.Dispose();
    }

    private void EnsureCurrentProjectionFitsBounds()
    {
        if (!TryFitCurrentProjectionToBounds())
        {
            m_pendingSceneFit = true;
        }
    }

    private bool TryFitCurrentProjectionToBounds()
    {
        if (m_viewport.ActualWidth <= 1.0 || m_viewport.ActualHeight <= 1.0)
        {
            return false;
        }

        if (m_viewport.Camera is HelixToolkit.Wpf.SharpDX.OrthographicCamera orthographicCamera)
        {
            FitOrthographicToBounds(orthographicCamera);
            m_pendingSceneFit = false;
            return true;
        }

        if (m_viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera perspectiveCamera)
        {
            FitPerspectiveToBounds(perspectiveCamera);
            m_pendingSceneFit = false;
            return true;
        }

        return false;
    }

    private void FitOrthographicToBounds(HelixToolkit.Wpf.SharpDX.OrthographicCamera orthographicCamera, double margin = 1.08)
    {
        MediaRect3D bounds = GetSceneBounds();
        if (bounds.IsEmpty)
        {
            return;
        }

        MediaPoint3D target = GetSceneCenter() ?? new MediaPoint3D(0.0, 0.0, 0.0);
        MediaVector3D direction = orthographicCamera.LookDirection;
        if (direction.LengthSquared <= 0.00001)
        {
            direction = GetPresetDirection(m_currentPreset);
        }

        direction.Normalize();
        MediaVector3D up = orthographicCamera.UpDirection;
        GetProjectedExtents(bounds, target, direction, up, out double halfWidth, out double halfHeight, out double halfDepth);

        double aspect = GetViewportAspectRatio();
        double width = Math.Max(halfWidth * 2.0, halfHeight * 2.0 * aspect) * margin;
        double distance = Math.Max((halfDepth * 2.5) + 1.0, 1.0);

        orthographicCamera.LookDirection = direction * distance;
        orthographicCamera.Position = target - orthographicCamera.LookDirection;
        orthographicCamera.UpDirection = NormalizeUp(direction, up);
        orthographicCamera.Width = width <= 0.00001 ? 1.0 : width;
        orthographicCamera.NearPlaneDistance = 0.001;
        orthographicCamera.FarPlaneDistance = Math.Max(distance + (halfDepth * 4.0), 200.0);
        m_viewport.FixedRotationPoint = target;
    }

    private void FitPerspectiveToBounds(HelixToolkit.Wpf.SharpDX.PerspectiveCamera perspectiveCamera, double margin = 1.12)
    {
        MediaRect3D bounds = GetSceneBounds();
        if (bounds.IsEmpty)
        {
            return;
        }

        MediaPoint3D target = GetSceneCenter() ?? new MediaPoint3D(0.0, 0.0, 0.0);

        MediaVector3D direction = perspectiveCamera.LookDirection;
        if (direction.LengthSquared <= 0.00001)
        {
            direction = GetPresetDirection(MeshViewportViewPreset.Perspective);
        }

        direction.Normalize();
        MediaVector3D up = NormalizeUp(direction, perspectiveCamera.UpDirection);
        GetProjectedExtents(bounds, target, direction, up, out double halfWidth, out double halfHeight, out double halfDepth);

        double verticalFovRadians = perspectiveCamera.FieldOfView * (Math.PI / 180.0);
        double horizontalFovRadians = 2.0 * Math.Atan(Math.Tan(verticalFovRadians * 0.5) * GetViewportAspectRatio());
        double distance = Math.Max(
                              halfHeight / Math.Tan(verticalFovRadians * 0.5),
                              halfWidth / Math.Tan(horizontalFovRadians * 0.5))
                          + halfDepth;
        if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0.00001)
        {
            distance = 5.0;
        }

        distance *= margin;
        perspectiveCamera.LookDirection = direction * distance;
        perspectiveCamera.Position = target - perspectiveCamera.LookDirection;
        perspectiveCamera.UpDirection = up;
        perspectiveCamera.NearPlaneDistance = 0.001;
        perspectiveCamera.FarPlaneDistance = Math.Max(distance + (halfDepth * 4.0), 200.0);
        m_viewport.FixedRotationPoint = target;
    }

    private void SwitchCamera(MeshViewportViewPreset preset)
    {
        bool useOrthographic = preset != MeshViewportViewPreset.Perspective;
        m_viewport.Orthographic = useOrthographic;
        m_viewport.Camera = useOrthographic ? m_orthographicCamera : m_perspectiveCamera;
    }

    private MediaPoint3D? GetSceneCenter()
    {
        MediaRect3D bounds = GetSceneBounds();
        if (bounds.IsEmpty)
        {
            return null;
        }

        return new MediaPoint3D(
            bounds.X + (bounds.SizeX * 0.5),
            bounds.Y + (bounds.SizeY * 0.5),
            bounds.Z + (bounds.SizeZ * 0.5));
    }

    private double GetDefaultCameraDistance()
    {
        MediaRect3D bounds = GetSceneBounds();
        if (bounds.IsEmpty)
        {
            return 5.0;
        }

        return Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ)) * 1.8;
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!m_pendingSceneFit || m_sceneData is null)
        {
            return;
        }

        TryFitCurrentProjectionToBounds();
    }

    private double GetViewportAspectRatio()
    {
        return m_viewport.ActualHeight > 0.00001
            ? Math.Max(m_viewport.ActualWidth / m_viewport.ActualHeight, 0.00001)
            : 1.0;
    }

    private static MediaVector3D NormalizeUp(MediaVector3D direction, MediaVector3D up)
    {
        if (up.LengthSquared <= 0.00001)
        {
            up = new MediaVector3D(0.0, 1.0, 0.0);
        }

        direction.Normalize();
        up.Normalize();
        MediaVector3D right = MediaVector3D.CrossProduct(direction, up);
        if (right.LengthSquared <= 0.00001)
        {
            up = Math.Abs(direction.Y) < 0.99
                ? new MediaVector3D(0.0, 1.0, 0.0)
                : new MediaVector3D(0.0, 0.0, 1.0);
            right = MediaVector3D.CrossProduct(direction, up);
        }

        right.Normalize();
        MediaVector3D normalizedUp = MediaVector3D.CrossProduct(right, direction);
        normalizedUp.Normalize();
        return normalizedUp;
    }

    private static void GetProjectedExtents(
        MediaRect3D bounds,
        MediaPoint3D target,
        MediaVector3D direction,
        MediaVector3D up,
        out double halfWidth,
        out double halfHeight,
        out double halfDepth)
    {
        direction.Normalize();
        MediaVector3D normalizedUp = NormalizeUp(direction, up);
        MediaVector3D right = MediaVector3D.CrossProduct(direction, normalizedUp);
        right.Normalize();

        halfWidth = 0.0;
        halfHeight = 0.0;
        halfDepth = 0.0;
        foreach (MediaPoint3D corner in EnumerateBoundsCorners(bounds))
        {
            MediaVector3D offset = corner - target;
            halfWidth = Math.Max(halfWidth, Math.Abs(MediaVector3D.DotProduct(offset, right)));
            halfHeight = Math.Max(halfHeight, Math.Abs(MediaVector3D.DotProduct(offset, normalizedUp)));
            halfDepth = Math.Max(halfDepth, Math.Abs(MediaVector3D.DotProduct(offset, direction)));
        }
    }

    private static IEnumerable<MediaPoint3D> EnumerateBoundsCorners(MediaRect3D bounds)
    {
        double minX = bounds.X;
        double minY = bounds.Y;
        double minZ = bounds.Z;
        double maxX = bounds.X + bounds.SizeX;
        double maxY = bounds.Y + bounds.SizeY;
        double maxZ = bounds.Z + bounds.SizeZ;

        yield return new MediaPoint3D(minX, minY, minZ);
        yield return new MediaPoint3D(minX, minY, maxZ);
        yield return new MediaPoint3D(minX, maxY, minZ);
        yield return new MediaPoint3D(minX, maxY, maxZ);
        yield return new MediaPoint3D(maxX, minY, minZ);
        yield return new MediaPoint3D(maxX, minY, maxZ);
        yield return new MediaPoint3D(maxX, maxY, minZ);
        yield return new MediaPoint3D(maxX, maxY, maxZ);
    }

    private static MediaVector3D GetPresetDirection(MeshViewportViewPreset preset)
    {
        return preset switch
        {
            MeshViewportViewPreset.Front => new MediaVector3D(0.0, 0.0, -1.0),
            MeshViewportViewPreset.Back => new MediaVector3D(0.0, 0.0, 1.0),
            MeshViewportViewPreset.Left => new MediaVector3D(-1.0, 0.0, 0.0),
            MeshViewportViewPreset.Right => new MediaVector3D(1.0, 0.0, 0.0),
            MeshViewportViewPreset.Top => new MediaVector3D(0.0, -1.0, 0.0),
            MeshViewportViewPreset.Bottom => new MediaVector3D(0.0, 1.0, 0.0),
            _ => new MediaVector3D(0.61, 0.37, -0.70)
        };
    }

    private static MediaVector3D GetPresetUpDirection(MeshViewportViewPreset preset)
    {
        return preset switch
        {
            MeshViewportViewPreset.Top => new MediaVector3D(0.0, 0.0, -1.0),
            MeshViewportViewPreset.Bottom => new MediaVector3D(0.0, 0.0, 1.0),
            _ => new MediaVector3D(0.0, 1.0, 0.0)
        };
    }

    private MaterialCore CreateMaterial(MeshViewportSectionData section)
    {
        string materialKey = BuildMaterialKey(section);
        if (m_materialCache.TryGetValue(materialKey, out MaterialCacheEntry? cached))
        {
            return cached.Material;
        }

        PhongMaterial material = new()
        {
            AmbientColor = Colors.Gray.ToColor4(),
            DiffuseColor = Colors.White.ToColor4(),
            SpecularColor = Colors.Black.ToColor4(),
            SpecularShininess = 0.0f,
            RenderShadowMap = true
        };

        if (section.DiffuseTexturePng is { Length: > 0 })
        {
            material.DiffuseMap = new TextureModel(new MemoryStream(section.DiffuseTexturePng, writable: false));
        }

        if (section.NormalTexturePng is { Length: > 0 })
        {
            material.NormalMap = new TextureModel(new MemoryStream(section.NormalTexturePng, writable: false));
            material.RenderNormalMap = true;
        }

        m_materialCache[materialKey] = new MaterialCacheEntry { Material = material };
        return material;
    }

    private bool CanReuseLoadedScene(MeshViewportSceneData scene)
    {
        if (!string.Equals(m_loadedObjPath, scene.ObjPath, StringComparison.OrdinalIgnoreCase) ||
            m_lodSections.Count != scene.Lods.Count)
        {
            return false;
        }

        for (int lodIndex = 0; lodIndex < scene.Lods.Count; lodIndex++)
        {
            if (m_lodSections[lodIndex].Count != scene.Lods[lodIndex].Sections.Count)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateSectionState(MeshViewportSceneData scene)
    {
        bool visibilityChanged = false;
        for (int lodIndex = 0; lodIndex < scene.Lods.Count && lodIndex < m_lodSections.Count; lodIndex++)
        {
            IReadOnlyList<MeshViewportSectionData> sections = scene.Lods[lodIndex].Sections;
            for (int sectionIndex = 0; sectionIndex < sections.Count && sectionIndex < m_lodSections[lodIndex].Count; sectionIndex++)
            {
                SectionVisualState current = m_lodSections[lodIndex][sectionIndex];
                MeshViewportSectionData next = sections[sectionIndex];
                visibilityChanged |= current.Visible != next.Visible;
                current.Visible = next.Visible;
                string nextMaterialKey = BuildMaterialKey(next);
                if (!string.Equals(current.MaterialKey, nextMaterialKey, StringComparison.Ordinal))
                {
                    MaterialCore material = CreateMaterial(next);
                    foreach (MeshNode node in current.Nodes)
                    {
                        if (!ReferenceEquals(node.Material, material))
                        {
                            node.Material = material;
                        }
                    }

                    current.Material = material;
                    current.MaterialKey = nextMaterialKey;
                }
            }
        }

        if (visibilityChanged)
        {
            m_sceneBounds = MediaRect3D.Empty;
        }
    }

    private void BuildImportedNodeLookup(HelixToolkitScene importedScene)
    {
        m_nodesByObjectName.Clear();
        m_importedSectionGroups.Clear();
        foreach (SceneNode rootItem in importedScene.Root.Items)
        {
            List<MeshNode> nodes = [];
            foreach (SceneNode node in rootItem.Traverse())
            {
                if (node is MeshNode meshNode)
                {
                    nodes.Add(meshNode);
                }
            }

            if (nodes.Count > 0)
            {
                m_importedSectionGroups.Add(nodes);
                RegisterImportedNodes(rootItem.Name, nodes);
                foreach (MeshNode meshNode in nodes)
                {
                    RegisterImportedNodes(meshNode.Name, nodes);
                }
            }
        }
    }

    private void RegisterImportedNodes(string name, List<MeshNode> nodes)
    {
        string key = NormalizeLookupKey(name);
        if (string.IsNullOrWhiteSpace(key) || m_nodesByObjectName.ContainsKey(key))
        {
            return;
        }

        m_nodesByObjectName[key] = nodes;
    }

    private bool TryGetImportedSectionNodes(MeshViewportSectionData section, int fallbackIndex, out List<MeshNode> nodes)
    {
        string sectionKey = NormalizeLookupKey(section.ObjectName);
        if (!string.IsNullOrWhiteSpace(sectionKey) &&
            m_nodesByObjectName.TryGetValue(sectionKey, out List<MeshNode>? namedNodes))
        {
            nodes = namedNodes;
            return true;
        }

        if (fallbackIndex >= 0 && fallbackIndex < m_importedSectionGroups.Count)
        {
            nodes = m_importedSectionGroups[fallbackIndex];
            return true;
        }

        nodes = [];
        return false;
    }

    private static void ApplyMaterialToNodes(IEnumerable<MeshNode> nodes, MaterialCore material)
    {
        foreach (MeshNode meshNode in nodes)
        {
            if (!ReferenceEquals(meshNode.Material, material))
            {
                meshNode.Material = material;
            }
        }
    }

    private static string NormalizeLookupKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private MediaRect3D GetSceneBounds()
    {
        if (!m_sceneBounds.IsEmpty)
        {
            return m_sceneBounds;
        }

        m_viewport.UpdateLayout();
        m_sceneBounds = m_viewport.FindBounds3D();
        return m_sceneBounds;
    }

    private static string BuildMaterialKey(MeshViewportSectionData section)
    {
        return string.Concat(
            section.MaterialId.ToString(),
            "|",
            BuildTextureKey(section.DiffuseTexturePng),
            "|",
            BuildTextureKey(section.NormalTexturePng));
    }

    private static string BuildTextureKey(byte[]? textureBytes)
    {
        if (textureBytes is not { Length: > 0 })
        {
            return "none";
        }

        return string.Concat(
            textureBytes.Length.ToString(),
            ":",
            Convert.ToHexString(SHA256.HashData(textureBytes)));
    }

    private static MaterialCore CreateFallbackMaterial()
    {
        return new PhongMaterialCore
        {
            AmbientColor = Colors.Gray.ToColor4(),
            DiffuseColor = new Color4(0.88f, 0.45f, 0.22f, 1.0f),
            SpecularColor = Colors.Black.ToColor4(),
            SpecularShininess = 0.0f,
            RenderShadowMap = true
        };
    }
}
