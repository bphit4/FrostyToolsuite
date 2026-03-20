using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Frosty.Sdk.Resources;

namespace FrostyEditor.Utils;

public enum TextureImageFormat
{
    PNG,
    TGA,
    HDR,
    DDS
}

[StructLayout(LayoutKind.Sequential)]
public struct TextureImportOptions
{
    public TextureType Type;
    public TextureUtils.DxgiFormat Format;
    [MarshalAs(UnmanagedType.I1)]
    public bool GenerateMipmaps;
    public int MipmapsFilter;
    [MarshalAs(UnmanagedType.I1)]
    public bool ResizeTexture;
    public int ResizeFilter;
    public int ResizeWidth;
    public int ResizeHeight;
}

[StructLayout(LayoutKind.Sequential)]
public struct BlobData
{
    private IntPtr data;
    private long size;

    public byte[] ToArray()
    {
        if (data == IntPtr.Zero || size <= 0)
        {
            return Array.Empty<byte>();
        }

        byte[] buffer = new byte[size];
        Marshal.Copy(data, buffer, 0, (int)size);
        return buffer;
    }
}

internal static class DxTexNative
{
    static DxTexNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(DxTexNative).Assembly, ResolveNativeLibrary);
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("dxtex.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "dxtex.dll"),
            Path.Combine(baseDirectory, "ThirdParty", "dxtex.dll")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("dxtex.dll", EntryPoint = "ConvertDDSToImage")]
    public static extern void ConvertDDSToImage(byte[] data, long dataSize, TextureImageFormat format, ref BlobData outData);

    [DllImport("dxtex.dll", EntryPoint = "ConvertDDSToImages")]
    public static extern void ConvertDDSToImages(byte[] data, long dataSize, TextureImageFormat format,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)]
        ref BlobData[] outDatas, int outCount);

    [DllImport("dxtex.dll", EntryPoint = "ConvertImageToDDS")]
    public static extern void ConvertImageToDDS(byte[] data, long dataSize, TextureImageFormat originalFormat, TextureImportOptions options, ref BlobData outData);

    [DllImport("dxtex.dll", EntryPoint = "ConvertImagesToDDS")]
    public static extern void ConvertImagesToDDS(byte[] data, long[] dataSizes, long count, TextureImageFormat originalFormat, TextureImportOptions options, ref BlobData outData);

    [DllImport("dxtex.dll", EntryPoint = "ReleaseBlob")]
    public static extern void ReleaseBlob(BlobData data);
}
