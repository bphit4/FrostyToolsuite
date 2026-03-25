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
using BoundingBox = SharpDX.BoundingBox;
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
    private readonly DirectionalLight3D m_fillLightLeft;
    private readonly DirectionalLight3D m_fillLightRight;
    private readonly DirectionalLight3D m_topFillLight;
    private readonly DirectionalLight3D m_upperRearFillLight;
    private readonly DirectionalLight3D m_lowerFillLight;
    private readonly AmbientLight3D m_ambientLight;
    private readonly MaterialCore m_fallbackMaterial;
    private const string LitSceneNamePrefix = "__frosty_lit__:";
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
            IsShadowMappingEnabled = false,
            Orthographic = false,
            CameraMode = CameraMode.Inspect,
            FixedRotationPointEnabled = true,
            // Keep zoom centered on the scene so very long/thin meshes do not drift off-screen
            // or become hard to recover after a few wheel steps.
            ZoomAroundMouseDownPoint = false,
            EffectsManager = m_effectsManager,
            Camera = m_perspectiveCamera,
            BackgroundColor = (MediaColor)ColorConverter.ConvertFromString("#161A1F")!
        };

        m_viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.RightClick)));
        m_viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.LeftClick)));
        m_viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Zoom, new MouseGesture(MouseAction.MiddleClick)));
        m_viewport.InputBindings.Add(new KeyBinding(ViewportCommands.ZoomExtents, new KeyGesture(Key.Z, ModifierKeys.Control)));
        m_viewport.CameraChanged += OnViewportCameraChanged;

        // Balanced studio-style lighting for skin and cloth: a warmer front key, soft side fills,
        // a restrained rear rim, and modest ambient to preserve texture detail.
        m_mainDirectionalLight = CreateDirectionalLight("#8A847A");
        m_fillLightLeft = CreateDirectionalLight("#51565E");
        m_fillLightRight = CreateDirectionalLight("#51565E");
        m_topFillLight = CreateDirectionalLight("#6A7078");
        m_upperRearFillLight = CreateDirectionalLight("#5A5A58");
        m_lowerFillLight = CreateDirectionalLight("#262A31");
        m_ambientLight = new AmbientLight3D { Color = (MediaColor)ColorConverter.ConvertFromString("#24272C")! };
        m_viewport.Items.Add(m_mainDirectionalLight);
        m_viewport.Items.Add(m_fillLightLeft);
        m_viewport.Items.Add(m_fillLightRight);
        m_viewport.Items.Add(m_topFillLight);
        m_viewport.Items.Add(m_upperRearFillLight);
        m_viewport.Items.Add(m_lowerFillLight);
        m_viewport.Items.Add(m_ambientLight);
        m_viewport.Items.Add(new Element3DPresenter { Content = m_groupModel });
        m_fallbackMaterial = CreateFallbackMaterial();
        UpdateLightingRig();

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

        bool litShading = IsLitShadingScene(scene);

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
        importer.Configuration.AssimpPostProcessSteps |=
            Assimp.PostProcessSteps.GenerateSmoothNormals |
            Assimp.PostProcessSteps.JoinIdenticalVertices;
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

                MaterialCore material = CreateMaterial(section, litShading);
                string materialKey = BuildMaterialKey(section, litShading);
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
        UpdateLightingEnabledState(texturesEnabled && m_sceneData is not null && IsLitShadingScene(m_sceneData));
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

        UpdateLightingRigFromCamera();
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
        m_viewport.CameraChanged -= OnViewportCameraChanged;
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
            UpdateDynamicClipPlanes();
            m_pendingSceneFit = false;
            return true;
        }

        if (m_viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera perspectiveCamera)
        {
            FitPerspectiveToBounds(perspectiveCamera);
            UpdateDynamicClipPlanes();
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

    private void OnViewportCameraChanged(object sender, RoutedEventArgs e)
    {
        UpdateLightingRigFromCamera();
        UpdateDynamicClipPlanes();
    }

    private double GetViewportAspectRatio()
    {
        return m_viewport.ActualHeight > 0.00001
            ? Math.Max(m_viewport.ActualWidth / m_viewport.ActualHeight, 0.00001)
            : 1.0;
    }

    private void UpdateLightingRig()
    {
        m_mainDirectionalLight.Direction = new MediaVector3D(-0.18, -0.34, -0.92);
        m_upperRearFillLight.Direction = new MediaVector3D(0.10, -0.12, 0.98);
        m_fillLightLeft.Direction = new MediaVector3D(0.88, -0.08, -0.46);
        m_fillLightRight.Direction = new MediaVector3D(-0.88, -0.08, -0.46);
        m_topFillLight.Direction = new MediaVector3D(0.0, -0.96, -0.28);
        m_lowerFillLight.Direction = new MediaVector3D(0.0, 0.92, -0.40);
    }

    private void UpdateLightingRigFromCamera()
    {
        if (m_viewport.Camera is not HelixToolkit.Wpf.SharpDX.Camera camera)
        {
            UpdateLightingRig();
            return;
        }

        MediaVector3D forward = camera.LookDirection;
        if (forward.LengthSquared <= 0.00001)
        {
            UpdateLightingRig();
            return;
        }

        forward.Normalize();
        MediaVector3D up = NormalizeUp(forward, camera.UpDirection);
        MediaVector3D right = MediaVector3D.CrossProduct(forward, up);
        if (right.LengthSquared <= 0.00001)
        {
            UpdateLightingRig();
            return;
        }

        right.Normalize();

        // Keep the lighting rig camera-relative so the visible side of the mesh
        // stays naturally readable regardless of whether the user is on front/back/side views.
        m_mainDirectionalLight.Direction = NormalizeDirection(forward + (-right * 0.18) + (-up * 0.30));
        m_fillLightLeft.Direction = NormalizeDirection(forward + (right * 0.55) + (-up * 0.08));
        m_fillLightRight.Direction = NormalizeDirection(forward + (-right * 0.55) + (-up * 0.08));
        m_topFillLight.Direction = NormalizeDirection(forward + (-up * 0.88));
        m_upperRearFillLight.Direction = NormalizeDirection((-forward * 0.92) + (-up * 0.12));
        m_lowerFillLight.Direction = NormalizeDirection(forward + (up * 0.68));
    }

    private void UpdateDynamicClipPlanes()
    {
        MediaRect3D bounds = GetSceneBounds();
        if (bounds.IsEmpty || m_viewport.Camera is not HelixToolkit.Wpf.SharpDX.Camera camera)
        {
            return;
        }

        MediaPoint3D center = new(
            bounds.X + (bounds.SizeX * 0.5),
            bounds.Y + (bounds.SizeY * 0.5),
            bounds.Z + (bounds.SizeZ * 0.5));

        double maxSize = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        double sceneRadius = Math.Max(maxSize * 0.5, 1.0);
        MediaVector3D forward = camera.LookDirection;
        if (forward.LengthSquared <= 0.00001)
        {
            return;
        }

        forward.Normalize();
        MediaVector3D toCenter = center - camera.Position;
        double centerDistance = Math.Abs(MediaVector3D.DotProduct(toCenter, forward));
        m_viewport.FixedRotationPoint = center;

        if (camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera perspectiveCamera)
        {
            perspectiveCamera.NearPlaneDistance = 0.0001;
            perspectiveCamera.FarPlaneDistance = Math.Max(centerDistance + (sceneRadius * 20.0), 10000.0);
            return;
        }

        if (camera is HelixToolkit.Wpf.SharpDX.OrthographicCamera orthographicCamera)
        {
            double orthoDistance = Math.Max(orthographicCamera.LookDirection.Length, sceneRadius * 2.0);
            orthographicCamera.NearPlaneDistance = 0.0001;
            orthographicCamera.FarPlaneDistance = Math.Max(orthoDistance + (sceneRadius * 20.0), 10000.0);
        }
    }

    private static DirectionalLight3D CreateDirectionalLight(string colorHex)
    {
        return new DirectionalLight3D
        {
            Color = (MediaColor)ColorConverter.ConvertFromString(colorHex)!,
            Direction = new MediaVector3D(0.0, 0.0, -1.0)
        };
    }

    private static MediaVector3D NormalizeDirection(MediaVector3D value)
    {
        if (value.LengthSquared <= 0.00001)
        {
            return new MediaVector3D(0.0, 0.0, -1.0);
        }

        value.Normalize();
        return value;
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

    private MaterialCore CreateMaterial(MeshViewportSectionData section, bool litShading)
    {
        string materialKey = BuildMaterialKey(section, litShading);
        if (m_materialCache.TryGetValue(materialKey, out MaterialCacheEntry? cached))
        {
            return cached.Material;
        }

        MaterialCore material;
        if (litShading)
        {
            PhongMaterialCore phongMaterial = new()
            {
                DiffuseColor = Colors.White.ToColor4(),
                AmbientColor = new Color4(0.10f, 0.10f, 0.10f, 1.0f),
                EmissiveColor = new Color4(0.018f, 0.018f, 0.018f, 1.0f),
                SpecularColor = new Color4(0.028f, 0.028f, 0.028f, 1.0f),
                SpecularShininess = 12.0f,
                ReflectiveColor = new Color4(0.0f, 0.0f, 0.0f, 1.0f),
                RenderShadowMap = false,
                RenderEnvironmentMap = false
            };

            if (section.DiffuseTexturePng is { Length: > 0 })
            {
                phongMaterial.DiffuseMap = new TextureModel(new MemoryStream(section.DiffuseTexturePng, writable: false));
                phongMaterial.RenderDiffuseMap = true;
            }

            material = phongMaterial;
        }
        else
        {
            DiffuseMaterialCore diffuseMaterial = new()
            {
                DiffuseColor = Colors.White.ToColor4(),
                EnableUnLit = true
            };

            if (section.DiffuseTexturePng is { Length: > 0 })
            {
                diffuseMaterial.DiffuseMap = new TextureModel(new MemoryStream(section.DiffuseTexturePng, writable: false));
                diffuseMaterial.RenderDiffuseMap = true;
            }

            material = diffuseMaterial;
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
                bool litShading = IsLitShadingScene(scene);
                string nextMaterialKey = BuildMaterialKey(next, litShading);
                if (!string.Equals(current.MaterialKey, nextMaterialKey, StringComparison.Ordinal))
                {
                    MaterialCore material = CreateMaterial(next, litShading);
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

        List<(string Key, List<MeshNode> Nodes)> orderedNamedGroups = [];
        Dictionary<string, List<MeshNode>> namedGroups = new(StringComparer.Ordinal);

        foreach (SceneNode rootItem in importedScene.Root.Items)
        {
            List<MeshNode> rootNodes = [];
            foreach (SceneNode node in rootItem.Traverse())
            {
                if (node is MeshNode meshNode)
                {
                    rootNodes.Add(meshNode);

                    string meshKey = NormalizeLookupKey(meshNode.Name);
                    if (string.IsNullOrWhiteSpace(meshKey))
                    {
                        continue;
                    }

                    if (!namedGroups.TryGetValue(meshKey, out List<MeshNode>? meshGroup))
                    {
                        meshGroup = [];
                        namedGroups.Add(meshKey, meshGroup);
                        orderedNamedGroups.Add((meshKey, meshGroup));
                    }

                    meshGroup.Add(meshNode);
                }
            }

            if (rootNodes.Count > 0)
            {
                RegisterImportedNodes(rootItem.Name, rootNodes);
            }
        }

        foreach ((string key, List<MeshNode> nodes) in orderedNamedGroups)
        {
            if (nodes.Count == 0)
            {
                continue;
            }

            m_importedSectionGroups.Add(nodes);
            if (!m_nodesByObjectName.ContainsKey(key))
            {
                m_nodesByObjectName.Add(key, nodes);
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

        bool found = false;
        Rect3D bounds = Rect3D.Empty;
        foreach (List<SectionVisualState> lod in m_lodSections)
        {
            foreach (SectionVisualState section in lod)
            {
                foreach (MeshNode node in section.Nodes)
                {
                    if (!node.Visible)
                    {
                        continue;
                    }

                    Rect3D nodeBounds = ToRect3D(node.BoundsWithTransform);
                    if (!found)
                    {
                        bounds = nodeBounds;
                        found = true;
                    }
                    else
                    {
                        bounds.Union(nodeBounds);
                    }
                }
            }
        }

        m_sceneBounds = found ? bounds : MediaRect3D.Empty;
        return m_sceneBounds;
    }

    private static Rect3D ToRect3D(BoundingBox bounds)
    {
        return new Rect3D(
            bounds.Minimum.X,
            bounds.Minimum.Y,
            bounds.Minimum.Z,
            bounds.Maximum.X - bounds.Minimum.X,
            bounds.Maximum.Y - bounds.Minimum.Y,
            bounds.Maximum.Z - bounds.Minimum.Z);
    }

    private void UpdateLightingEnabledState(bool litShading)
    {
        float litIntensity = litShading ? 1.0f : 0.0f;
        m_mainDirectionalLight.Color = ScaleColor((MediaColor)ColorConverter.ConvertFromString("#8A847A")!, litIntensity);
        m_fillLightLeft.Color = ScaleColor((MediaColor)ColorConverter.ConvertFromString("#51565E")!, litIntensity);
        m_fillLightRight.Color = ScaleColor((MediaColor)ColorConverter.ConvertFromString("#51565E")!, litIntensity);
        m_topFillLight.Color = ScaleColor((MediaColor)ColorConverter.ConvertFromString("#6A7078")!, litIntensity);
        m_upperRearFillLight.Color = ScaleColor((MediaColor)ColorConverter.ConvertFromString("#5A5A58")!, litIntensity);
        m_lowerFillLight.Color = ScaleColor((MediaColor)ColorConverter.ConvertFromString("#262A31")!, litIntensity);
        m_ambientLight.Color = litShading
            ? (MediaColor)ColorConverter.ConvertFromString("#24272C")!
            : (MediaColor)ColorConverter.ConvertFromString("#000000")!;
    }

    private static MediaColor ScaleColor(MediaColor color, float intensity)
    {
        byte Scale(byte component) => (byte)Math.Clamp((int)Math.Round(component * intensity), 0, 255);
        return MediaColor.FromArgb(color.A, Scale(color.R), Scale(color.G), Scale(color.B));
    }

    private static bool IsLitShadingScene(MeshViewportSceneData scene)
    {
        return scene.Name.StartsWith(LitSceneNamePrefix, StringComparison.Ordinal);
    }

    private static string BuildMaterialKey(MeshViewportSectionData section, bool litShading)
    {
        return string.Concat(
            section.MaterialId.ToString(),
            "|",
            litShading ? "lit" : "base",
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
        return new DiffuseMaterialCore
        {
            DiffuseColor = new Color4(0.88f, 0.45f, 0.22f, 1.0f),
            EnableUnLit = true
        };
    }
}
