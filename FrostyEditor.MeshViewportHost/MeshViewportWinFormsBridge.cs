using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace FrostyEditor.MeshViewportHost;

public sealed class MeshViewportWinFormsBridge : UserControl
{
    private readonly ElementHost m_elementHost;
    private readonly HelixMeshViewportControl m_viewportControl;

    public MeshViewportWinFormsBridge()
    {
        Dock = DockStyle.Fill;
        m_viewportControl = new HelixMeshViewportControl();
        m_elementHost = new ElementHost
        {
            Dock = DockStyle.Fill,
            Child = m_viewportControl
        };

        Controls.Add(m_elementHost);
    }

    public void LoadScene(MeshViewportSceneData scene)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<MeshViewportSceneData>(LoadScene), scene);
            return;
        }

        m_viewportControl.LoadScene(scene);
    }

    public void ApplySceneSettings(int currentLod, bool texturesEnabled, bool wireframe)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<int, bool, bool>(ApplySceneSettings), currentLod, texturesEnabled, wireframe);
            return;
        }

        m_viewportControl.ApplySceneSettings(currentLod, texturesEnabled, wireframe);
    }

    public void ApplyView(MeshViewportViewPreset preset)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<MeshViewportViewPreset>(ApplyView), preset);
            return;
        }

        m_viewportControl.ApplyView(preset);
    }

    public void ResetView()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ResetView));
            return;
        }

        m_viewportControl.ResetView();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Controls.Remove(m_elementHost);
            m_elementHost.Child = null;
            m_viewportControl.Dispose();
            m_elementHost.Dispose();
        }

        base.Dispose(disposing);
    }
}
