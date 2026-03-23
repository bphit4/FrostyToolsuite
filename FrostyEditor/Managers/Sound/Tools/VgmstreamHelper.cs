using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FrostyEditor.Managers.Sound.Tools;

public static class VgmstreamHelper
{
    private static Task? s_initialisationTask;
    private static readonly string s_toolFolderName = $"vgmstream-{Process.GetCurrentProcess().Id}";

    public static bool IsInitialised { get; private set; }

    public static string CliPath => Path.Combine(ToolRoot, "vgmstream-cli.exe");

    private static string ToolRoot { get; set; } = Path.Combine(Path.GetTempPath(), "FrostyToolsuite", "SoundTools", s_toolFolderName);

    public static Task InitialiseAsync(bool reinitialise = false)
    {
        if (reinitialise)
        {
            Task? task = s_initialisationTask;
            if (task is null || task.IsCompleted)
            {
                try
                {
                    task?.GetAwaiter().GetResult();
                }
                catch
                {
                    // Best-effort reset.
                }

                s_initialisationTask = null;
                IsInitialised = false;
            }
        }

        return s_initialisationTask ??= InitialiseInternalAsync();
    }

    public static async Task DecodeAsync(string inputFilePath, string outputFilePath, bool subsongs = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputFilePath);
        ArgumentNullException.ThrowIfNull(outputFilePath);

        await InitialiseAsync().ConfigureAwait(false);

        string arguments = $"{(subsongs ? "-S 0 " : string.Empty)}-o \"{outputFilePath}\" \"{inputFilePath}\"";
        await AudioToolBootstrap.RunProcessAsync(CliPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InitialiseInternalAsync()
    {
        ToolRoot = await AudioToolBootstrap.StageArchiveAsync(
            "Fifa_Tool.lib.vgmstream.vgmstream.zip",
            "Fifa_Tool.lib.vgmstream.vgmstream.zip",
            s_toolFolderName).ConfigureAwait(false);
        IsInitialised = true;
    }
}
