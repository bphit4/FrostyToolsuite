using System.Collections.ObjectModel;

namespace FrostyEditor.Models;

public sealed class HomePageContent
{
    public ObservableCollection<HomePageLinkItem> Links { get; } =
    [
        new("Frosty Tool Suite Support", "https://discord.gg/sB8ZUAT"),
        new("Frosty Base Source Code", "https://github.com/FrostyToolsuite/FrostyToolsuite"),
        new("Intel Texture Works Plugin", "https://software.intel.com/en-us/articles/intel-texture-works-plugin"),
        new("GPUOpen Compressonator", "https://github.com/GPUOpen-Tools/Compressonator/releases"),
        new("DirectXTex Releases", "https://github.com/microsoft/DirectXTex/releases"),
        new("DirectX End-User Runtime", "https://www.microsoft.com/en-au/download/details.aspx?id=35")
    ];

    public ObservableCollection<HomePageChangelogTab> ChangelogTabs { get; } =
    [
        new(
            "FrostyToolsuite 2.0",
            "The 2.0 shell is focused on bringing old Frosty behavior back into the Avalonia rewrite while keeping workflows responsive.",
            """
            v2.0 local changes
            ------------------
            - Rebuilt the editor shell with Data Explorer, documents, and output panes
            - Added project save, open, export, and launch wiring
            - Added texture array DDS export and import fixes
            - Restored dense inspector rows, single-click nodes, and row-wide editing
            - Added copy, copy value, paste, and object paste flows
            - Added modified-state tracking for assets and edited EBX rows
            - Added pointer-row actions, selectable logs, and admin launch manifest
            - Continuing to align EBX and texture inspectors with old Frosty behavior
            """)
    ];
}

public sealed class HomePageLinkItem
{
    public string Label { get; }
    public string Url { get; }

    public HomePageLinkItem(string inLabel, string inUrl)
    {
        Label = inLabel;
        Url = inUrl;
    }
}

public sealed class HomePageChangelogTab
{
    public string Title { get; }
    public string Intro { get; }
    public string Body { get; }

    public HomePageChangelogTab(string inTitle, string inIntro, string inBody)
    {
        Title = inTitle;
        Intro = inIntro;
        Body = inBody;
    }
}
