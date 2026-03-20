using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using FrostyEditor.Managers;
using FrostyEditor.Models;

namespace FrostyEditor.ViewModels;

public sealed class LegacyAssetDocumentViewModel : ViewModelBase
{
    private static readonly string[] s_textExtensions =
    [
        "TXT", "JSON", "XML", "CSV", "TSV", "LUA", "INI", "CFG", "LOC", "HTML", "HTM", "JS", "CSS"
    ];

    public string Header => m_asset.FileNameWithExtension;
    public string Name => m_asset.Name;
    public string Type => m_asset.Type;
    public string Path => m_asset.Path;
    public string FullName => m_asset.FullName;
    public string SizeText => $"{m_asset.Size:N0} bytes";
    public string InstanceCountText => m_asset.CollectorInstances.Count.ToString("N0", CultureInfo.InvariantCulture);
    public string PreviewTitle { get; }
    public string PreviewText { get; }
    public string Summary { get; }

    private readonly LegacyAssetModel m_asset;

    public LegacyAssetDocumentViewModel(LegacyAssetModel asset)
    {
        m_asset = asset;
        byte[] data = LegacyAssetLoader.ReadAssetBytes(asset);

        if (ShouldUseTextPreview(asset, data))
        {
            PreviewTitle = "Text Preview";
            PreviewText = BuildTextPreview(asset, data);
            Summary = "Read-only legacy text preview. Export is available from the asset context menu.";
        }
        else
        {
            PreviewTitle = "Binary Preview";
            PreviewText = BuildHexPreview(data);
            Summary = asset.Type.Equals("DB", StringComparison.OrdinalIgnoreCase)
                ? "Read-only binary DB preview. The old dedicated legacy DB editor still needs a full port."
                : "Read-only binary preview. Export is available from the asset context menu.";
        }
    }

    private static bool ShouldUseTextPreview(LegacyAssetModel asset, byte[] data)
    {
        if (s_textExtensions.Contains(asset.Type, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (data.Length == 0)
        {
            return true;
        }

        int sampleLength = Math.Min(data.Length, 1024);
        int printable = 0;
        for (int i = 0; i < sampleLength; i++)
        {
            byte value = data[i];
            if (value == 9 || value == 10 || value == 13 || (value >= 32 && value <= 126))
            {
                printable++;
            }
        }

        return printable >= sampleLength * 0.9;
    }

    private static string BuildTextPreview(LegacyAssetModel asset, byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);

        if (asset.Type.Equals("JSON", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
            }
        }

        return text;
    }

    private static string BuildHexPreview(byte[] data)
    {
        const int bytesPerLine = 16;
        int length = Math.Min(data.Length, 4096);
        StringBuilder builder = new();

        for (int offset = 0; offset < length; offset += bytesPerLine)
        {
            int sliceLength = Math.Min(bytesPerLine, length - offset);
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
            builder.Append("  ");

            for (int i = 0; i < bytesPerLine; i++)
            {
                if (i < sliceLength)
                {
                    builder.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("  ");
                }

                if (i < bytesPerLine - 1)
                {
                    builder.Append(' ');
                }
            }

            builder.Append("  ");

            for (int i = 0; i < sliceLength; i++)
            {
                byte value = data[offset + i];
                builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }

            builder.AppendLine();
        }

        if (data.Length > length)
        {
            builder.AppendLine();
            builder.Append($"... preview truncated at {length:N0} bytes of {data.Length:N0}.");
        }

        return builder.ToString();
    }
}
