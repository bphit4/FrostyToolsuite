using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using FrostyEditor.Managers;

namespace FrostyEditor.Models;

public sealed class LegacyAssetModel
{
    public sealed record CollectorInstance(
        Guid ChunkId,
        long Offset,
        long Size,
        long CompressedOffset,
        long CompressedSize,
        string CollectorName);

    private readonly List<CollectorInstance> m_collectorInstances = new();

    public string FullName { get; }
    public string Name { get; }
    public string Type { get; }
    public string Path { get; }
    public string FileNameWithExtension { get; }
    public Bitmap Icon => AssetIconRegistry.GetIcon(Type);
    public bool IsModified => false;
    public bool IsAdded => false;
    public bool IsUnsaved => false;
    public bool IsUnmodified => true;
    public IReadOnlyList<CollectorInstance> CollectorInstances => m_collectorInstances;
    public CollectorInstance PrimaryInstance => m_collectorInstances[0];
    public long Size => m_collectorInstances.Count == 0 ? 0 : m_collectorInstances[0].Size;

    public LegacyAssetModel(string fullName)
    {
        string normalizedName = fullName.Replace('\\', '/');
        FullName = normalizedName;
        FileNameWithExtension = System.IO.Path.GetFileName(normalizedName);
        Path = System.IO.Path.GetDirectoryName(normalizedName.Replace('/', System.IO.Path.DirectorySeparatorChar))?
            .Replace(System.IO.Path.DirectorySeparatorChar, '/') ?? string.Empty;

        string extension = System.IO.Path.GetExtension(FileNameWithExtension);
        Type = string.IsNullOrWhiteSpace(extension) ? "LEGACY" : extension.TrimStart('.').ToUpperInvariant();
        Name = string.IsNullOrWhiteSpace(extension)
            ? FileNameWithExtension
            : System.IO.Path.GetFileNameWithoutExtension(FileNameWithExtension);
    }

    public void AddInstance(CollectorInstance instance)
    {
        m_collectorInstances.Add(instance);
    }

    public static Comparison<LegacyAssetModel?> SortAscending<T>(Func<LegacyAssetModel, T> selector)
    {
        return (x, y) =>
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            return Comparer<T>.Default.Compare(selector(x), selector(y));
        };
    }

    public static Comparison<LegacyAssetModel?> SortDescending<T>(Func<LegacyAssetModel, T> selector)
    {
        return (x, y) =>
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return 1;
            if (y is null)
                return -1;

            return Comparer<T>.Default.Compare(selector(y), selector(x));
        };
    }
}
