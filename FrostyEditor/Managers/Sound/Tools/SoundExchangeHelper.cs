using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FrostyEditor.Managers.Sound.Tools;

internal enum SoundExchangeCodec
{
    S16bInt = 2,
    Eaxma = 3,
    XasInt = 4,
    Ealayer3Int = 5,
    Ealayer3PcmInt = 6,
    Ealayer3SpikeInt = 7,
    Easpeex = 9,
    Eamp3 = 11,
    Eaopus = 12,
    Eaatrac9 = 13,
    MultistreamOpus = 14,
    MultistreamOpusUncoupled = 15
}

public static class SoundExchangeHelper
{
    private static readonly Guid s_fileGuid = new("{90467CB7-B85C-4A8C-89D0-9610E10114F5}");
    private static Task? s_initialisationTask;
    private static readonly string s_toolFolderName = $"sx-{Process.GetCurrentProcess().Id}";

    public static bool IsInitialised { get; private set; }

    public static string SoundExchangePath => Path.Combine(ToolRoot, s_fileGuid.ToString("N"));

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

    public static async Task ConvertAsync(
        string inputFilePath,
        string outputFilePath,
        int? codec = null,
        bool seekable = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputFilePath);
        ArgumentNullException.ThrowIfNull(outputFilePath);

        await InitialiseAsync().ConfigureAwait(false);

        string codecToken = GetCodecToken(GetSoundExchangeFormat(codec));
        string arguments = $"-sndplayer -fileformatversion1 -{codecToken} {(seekable ? "-seekable " : string.Empty)}\"{inputFilePath}\" -=\"{outputFilePath}\"";
        await AudioToolBootstrap.RunProcessAsync(SoundExchangePath, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InitialiseInternalAsync()
    {
        ToolRoot = await AudioToolBootstrap.StageArchiveAsync(
            "Fifa_Tool.lib.sx.sx.zip",
            "Fifa_Tool.lib.sx.sx.zip",
            s_toolFolderName).ConfigureAwait(false);
        IsInitialised = true;
    }

    private static SoundExchangeCodec GetSoundExchangeFormat(int? codec)
    {
        if (!codec.HasValue)
        {
            return SoundExchangeCodec.MultistreamOpus;
        }

        if (Enum.IsDefined(typeof(SoundExchangeCodec), codec.Value))
        {
            return (SoundExchangeCodec)codec.Value;
        }

        return SoundExchangeCodec.MultistreamOpus;
    }

    private static string GetCodecToken(SoundExchangeCodec codec)
    {
        return codec switch
        {
            SoundExchangeCodec.S16bInt => "s16b_int",
            SoundExchangeCodec.Eaxma => "eaxma",
            SoundExchangeCodec.XasInt => "xas_int",
            SoundExchangeCodec.Ealayer3Int => "ealayer3_int",
            SoundExchangeCodec.Ealayer3PcmInt => "ealayer3pcm_int",
            SoundExchangeCodec.Ealayer3SpikeInt => "ealayer3spike_int",
            SoundExchangeCodec.Easpeex => "easpeex",
            SoundExchangeCodec.Eamp3 => "eamp3",
            SoundExchangeCodec.Eaopus => "eaopus",
            SoundExchangeCodec.Eaatrac9 => "eaatrac9",
            SoundExchangeCodec.MultistreamOpus => "multistreamopus",
            SoundExchangeCodec.MultistreamOpusUncoupled => "multistreamopusuncoupled",
            _ => "multistreamopus"
        };
    }
}
