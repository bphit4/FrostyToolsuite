using System;
using Avalonia.Controls;
using Avalonia.Platform;
using FrostyEditor.MeshViewportHost;

namespace FrostyEditor.Controls;

public sealed class WindowsMeshViewportHost : NativeControlHost
{
    private MeshViewportWinFormsBridge? m_bridge;
    private MeshViewportSceneData? m_scene;
    private int m_currentLod;
    private bool m_texturesEnabled = true;
    private bool m_wireframe;
    private MeshViewportViewPreset m_viewPreset = MeshViewportViewPreset.Perspective;

    public WindowsMeshViewportHost()
    {
        DetachedFromVisualTree += (_, _) => ReleaseBridge();
    }

    public void LoadScene(MeshViewportSceneData scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        m_scene = scene;
        m_currentLod = scene.CurrentLod;
        m_texturesEnabled = scene.TexturesEnabled;
        m_wireframe = scene.Wireframe;
        m_viewPreset = scene.ViewPreset;
        ReplayState();
    }

    public void ApplySceneSettings(int currentLod, bool texturesEnabled, bool wireframe)
    {
        m_currentLod = currentLod;
        m_texturesEnabled = texturesEnabled;
        m_wireframe = wireframe;
        m_bridge?.ApplySceneSettings(currentLod, texturesEnabled, wireframe);
    }

    public void ApplyView(MeshViewportViewPreset preset)
    {
        m_viewPreset = preset;
        m_bridge?.ApplyView(preset);
    }

    public void ResetView()
    {
        m_bridge?.ResetView();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        ReleaseBridge();
        m_bridge = new MeshViewportWinFormsBridge();
        m_bridge.CreateControl();
        ReplayState();
        return new PlatformHandle(m_bridge.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ReleaseBridge();
    }

    private void ReplayState()
    {
        if (m_bridge is null || m_scene is null)
        {
            return;
        }

        MeshViewportSceneData replayScene = new()
        {
            Name = m_scene.Name,
            ObjPath = m_scene.ObjPath,
            CurrentLod = m_currentLod,
            TexturesEnabled = m_texturesEnabled,
            Wireframe = m_wireframe,
            ViewPreset = m_viewPreset,
            Lods = m_scene.Lods
        };

        m_bridge.LoadScene(replayScene);
    }

    private void ReleaseBridge()
    {
        if (m_bridge is null)
        {
            return;
        }

        try
        {
            m_bridge.Dispose();
        }
        finally
        {
            m_bridge = null;
        }
    }
}
