namespace FrostyEditor;

public sealed class AppStartupOptions
{
    public static AppStartupOptions Empty { get; } = new();

    public string? ProfileKey { get; init; }
    public string? InitFsKeyPath { get; init; }
    public bool AutoSelectProfile { get; init; }

    public static AppStartupOptions Parse(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return Empty;
        }

        string? profileKey = null;
        string? initFsKeyPath = null;
        bool autoSelectProfile = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--profile":
                case "-profile":
                    if (i + 1 < args.Length)
                    {
                        profileKey = args[++i];
                    }
                    break;

                case "--initfs-key":
                case "-initfskey":
                case "--key":
                    if (i + 1 < args.Length)
                    {
                        initFsKeyPath = args[++i];
                    }
                    break;

                case "--auto-select":
                case "-autoselect":
                    autoSelectProfile = true;
                    break;
            }
        }

        if (!autoSelectProfile && !string.IsNullOrWhiteSpace(profileKey))
        {
            autoSelectProfile = true;
        }

        return new AppStartupOptions
        {
            ProfileKey = profileKey,
            InitFsKeyPath = initFsKeyPath,
            AutoSelectProfile = autoSelectProfile
        };
    }
}
