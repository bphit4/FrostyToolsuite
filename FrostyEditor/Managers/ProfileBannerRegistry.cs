using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FrostyEditor.Managers;

public static class ProfileBannerRegistry
{
    private const string BannerBasePath = "avares://FrostyEditor/Assets/Banners/";
    private const string FallbackIconPath = "avares://FrostyEditor/Assets/FrostySnowflake.png";

    private static readonly Dictionary<string, string> s_bannerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fifa17"] = "FIFA17.png",
        ["fifa18"] = "FIFA18.png",
        ["fifa19"] = "fifa19.png",
        ["fifa20"] = "fifa20.png",
        ["fifa21"] = "fifa21.png",
        ["fifa22"] = "fifa22.png",
        ["fifa23"] = "fifa23.png",
        ["madden19"] = "madden19.png",
        ["madden20"] = "madden20.png",
        ["madden22"] = "madden22.png",
        ["madden23"] = "madden23.png",
        ["madden24"] = "madden24.png",
        ["madden25"] = "madden25.png",
        ["madden26"] = "madden26.png",
        ["pgatour"] = "PGATour.png",
        ["pga"] = "pga.png",
    };

    private static readonly Dictionary<string, Bitmap?> s_bannerCache = new(StringComparer.OrdinalIgnoreCase);
    private static Bitmap? s_fallbackIcon;

    public static Bitmap? GetBanner(string? profileKey)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            return null;
        }

        if (s_bannerCache.TryGetValue(profileKey, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap? banner = null;
        if (s_bannerFiles.TryGetValue(profileKey, out string? fileName))
        {
            banner = LoadBitmap(BannerBasePath + fileName);
        }

        s_bannerCache[profileKey] = banner;
        return banner;
    }

    public static Bitmap GetFallbackIcon()
    {
        s_fallbackIcon ??= LoadBitmap(FallbackIconPath) ?? throw new FileNotFoundException("Could not load Frosty fallback icon.");
        return s_fallbackIcon;
    }

    private static Bitmap? LoadBitmap(string uri)
    {
        Uri assetUri = new(uri);
        if (!AssetLoader.Exists(assetUri))
        {
            return null;
        }

        using Stream stream = AssetLoader.Open(assetUri);
        return new Bitmap(stream);
    }
}
