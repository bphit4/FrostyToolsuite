using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Frosty.Sdk.Utils;
using Microsoft.Win32.SafeHandles;

namespace Frosty.Sdk.IO;

public sealed unsafe partial class MemoryReader : IDisposable
{
    #region -- Windows --

    private enum SystemErrorCode
    {
        InvalidParameter = 0x57,
        PartialCopy = 0x12B
    }

    [Flags]
    private enum ProcessAccessType
    {
        VmRead = 0x0010,
        QueryInformation = 0x0400
    }

    [Flags]
    private enum AllocationType
    {
        Commit = 0x1000
    }

    [Flags]
    private enum ProtectionType
    {
        NoAccess = 0x1,
        Guard = 0x100
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(SafeProcessHandle processHandle, nint address, nint bytes, nint size, out nint bytesReadCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(ProcessAccessType desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualQueryEx(SafeProcessHandle processHandle, nint address, out MemoryBasicInformation64 memoryInformation, nint size);

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private readonly record struct MemoryBasicInformation64([field: FieldOffset(0x0)] long BaseAddress, [field: FieldOffset(0x18)] nint RegionSize, [field: FieldOffset(0x20)] AllocationType State, [field: FieldOffset(0x24)] ProtectionType Protect);

    #endregion

    #region -- Linux --

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec
    {
        public void* iov_base;
        public nuint iov_len;
    }

    [LibraryImport("libc")]
    private static unsafe partial nint process_vm_readv(int pid, Iovec* localIov, nuint localIovCount, Iovec* remoteIov,
        nuint remoteIovCount, nuint flags);

    #endregion

    public long Position { get; set; }
    public int LastPatternScanRegionCount { get; private set; }
    public int LastPatternScanSkippedRegionCount { get; private set; }
    public int LastPatternScanPartialReadCount { get; private set; }

    private readonly Process m_process;
    private readonly SafeProcessHandle m_processHandle;
    private readonly bool m_ownsProcessHandle;

    public MemoryReader(Process inProcess)
    {
        m_process = inProcess;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            m_processHandle = OpenProcess(ProcessAccessType.VmRead | ProcessAccessType.QueryInformation, false, inProcess.Id);
            if (!m_processHandle.IsInvalid)
            {
                m_ownsProcessHandle = true;
                return;
            }

            m_processHandle.Dispose();
        }

        m_processHandle = inProcess.SafeHandle;
    }

    public void Dispose()
    {
        if (m_ownsProcessHandle)
        {
            m_processHandle.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public void Pad(int alignment)
    {
        if (Position % alignment != 0)
        {
            Position += alignment - Position % alignment;
        }
    }

    public byte ReadByte()
    {
        Span<byte> buffer = stackalloc byte[sizeof(byte)];
        ReadExactly(buffer);
        return buffer[0];
    }

    public short ReadShort(bool pad = true)
    {
        if (pad)
        {
            Pad(sizeof(short));
        }

        Span<byte> buffer = stackalloc byte[sizeof(short)];
        ReadExactly(buffer);

        return BinaryPrimitives.ReadInt16LittleEndian(buffer);
    }

    public ushort ReadUShort(bool pad = true)
    {
        return (ushort)ReadShort(pad);
    }

    public int ReadInt(bool pad = true)
    {
        if (pad)
        {
            Pad(sizeof(int));
        }

        Span<byte> buffer = stackalloc byte[sizeof(int)];
        ReadExactly(buffer);

        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    public uint ReadUInt(bool pad = true)
    {
        return (uint)ReadInt(pad);
    }

    public long ReadLong(bool pad = true)
    {
        if (pad)
        {
            Pad(sizeof(long));
        }

        Span<byte> buffer = stackalloc byte[sizeof(long)];
        ReadExactly(buffer);

        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    public ulong ReadULong(bool pad = true)
    {
        return (ulong)ReadLong(pad);
    }

    public float ReadSingle(bool pad = true)
    {
        if (pad)
        {
            Pad(sizeof(float));
        }

        Span<byte> buffer = stackalloc byte[sizeof(float)];
        ReadExactly(buffer);

        return BinaryPrimitives.ReadSingleLittleEndian(buffer);
    }

    public double ReadDouble(bool pad = true)
    {
        if (pad)
        {
            Pad(sizeof(double));
        }

        Span<byte> buffer = stackalloc byte[sizeof(double)];
        ReadExactly(buffer);

        return BinaryPrimitives.ReadDoubleLittleEndian(buffer);
    }

    public Guid ReadGuid(bool pad = true)
    {
        if (pad)
        {
            Pad(4);
        }

        Span<byte> span = stackalloc byte[sizeof(Guid)];
        ReadExactly(span);

        return new Guid(span);
    }

    public Sha1 ReadSha1()
    {
        Span<byte> span = stackalloc byte[sizeof(Sha1)];
        ReadExactly(span);

        return new Sha1(span);
    }

    public string ReadNullTerminatedString(bool pad = true)
    {
        if (pad)
        {
            Pad(sizeof(long));
        }

        long offset = ReadLong();
        long orig = Position;
        Position = offset;

        StringBuilder sb = new();
        while (true)
        {
            char c = (char)ReadByte();
            if (c == 0x00)
            {
                break;
            }

            sb.Append(c);
        }

        Position = orig;
        return sb.ToString();
    }

    public void ReadExactly(Span<byte> buffer)
    {
        ReadMemory((nint)Position, buffer, out nint bytesRead);
        if (bytesRead != buffer.Length)
        {
            throw new EndOfStreamException();
        }
        Position += bytesRead;
    }

    public nint ScanPatter(string pattern)
    {
        ConvertPatternToAob(pattern, out string mask, out Block<byte> currentAob);
        LastPatternScanRegionCount = 0;
        LastPatternScanSkippedRegionCount = 0;
        LastPatternScanPartialReadCount = 0;

        foreach ((nint Address, int Size) region in GetRegions())
        {
            LastPatternScanRegionCount++;
            Block<byte> regionBytes = new(region.Size);
            if (!TryReadMemory(region.Address, regionBytes, out nint bytesRead))
            {
                regionBytes.Dispose();
                LastPatternScanSkippedRegionCount++;
                continue;
            }

            if (bytesRead < region.Size)
            {
                LastPatternScanPartialReadCount++;
            }

            int address = SearchPattern(regionBytes, 0, (int)bytesRead, currentAob, mask);
            if (address >= 0)
            {
                currentAob.Dispose();
                regionBytes.Dispose();
                return region.Address + address;
            }

            regionBytes.Dispose();
        }

        currentAob.Dispose();

        return nint.Zero;
    }

    public nint ScanPatternInMainModule(string pattern, int chunkSize = 1024 * 1024)
    {
        ConvertPatternToAob(pattern, out string mask, out Block<byte> currentAob);
        LastPatternScanRegionCount = 0;
        LastPatternScanSkippedRegionCount = 0;
        LastPatternScanPartialReadCount = 0;

        ProcessModule? mainModule = m_process.MainModule;
        if (mainModule is null || mainModule.ModuleMemorySize <= 0)
        {
            currentAob.Dispose();
            return nint.Zero;
        }

        nint moduleBase = mainModule.BaseAddress;
        int moduleSize = mainModule.ModuleMemorySize;
        int overlap = Math.Max(currentAob.Size - 1, 0);

        for (int offset = 0; offset < moduleSize; offset += chunkSize)
        {
            int bytesToRead = Math.Min(chunkSize + overlap, moduleSize - offset);
            if (bytesToRead <= 0)
            {
                break;
            }

            LastPatternScanRegionCount++;

            Block<byte> chunkBytes = new(bytesToRead);
            if (!TryReadMemory(moduleBase + offset, chunkBytes, out nint bytesRead))
            {
                chunkBytes.Dispose();
                LastPatternScanSkippedRegionCount++;
                continue;
            }

            if (bytesRead < bytesToRead)
            {
                LastPatternScanPartialReadCount++;
            }

            int address = SearchPattern(chunkBytes, 0, (int)bytesRead, currentAob, mask);
            if (address >= 0)
            {
                currentAob.Dispose();
                chunkBytes.Dispose();
                return moduleBase + offset + address;
            }

            chunkBytes.Dispose();
        }

        currentAob.Dispose();
        return nint.Zero;
    }

    public nint ScanPatternFromMainModuleBase(string pattern, int chunkSize = 1024 * 1024)
    {
        ConvertPatternToAob(pattern, out string mask, out Block<byte> currentAob);
        LastPatternScanRegionCount = 0;
        LastPatternScanSkippedRegionCount = 0;
        LastPatternScanPartialReadCount = 0;

        ProcessModule? mainModule = m_process.MainModule;
        if (mainModule is null)
        {
            currentAob.Dispose();
            return nint.Zero;
        }

        nint currentAddress = mainModule.BaseAddress;
        int overlap = Math.Max(currentAob.Size - 1, 0);

        while (true)
        {
            LastPatternScanRegionCount++;

            Block<byte> chunkBytes = new(chunkSize + overlap);
            if (!TryReadMemory(currentAddress, chunkBytes, out nint bytesRead))
            {
                chunkBytes.Dispose();
                LastPatternScanSkippedRegionCount++;
                break;
            }

            if (bytesRead < chunkSize + overlap)
            {
                LastPatternScanPartialReadCount++;
            }

            int address = SearchPattern(chunkBytes, 0, (int)bytesRead, currentAob, mask);
            if (address >= 0)
            {
                currentAob.Dispose();
                chunkBytes.Dispose();
                return currentAddress + address;
            }

            chunkBytes.Dispose();
            currentAddress += chunkSize;
        }

        currentAob.Dispose();
        return nint.Zero;
    }

    private int SearchPattern(Block<byte> buffer, int initIndex, int bufferLength, Block<byte> currentAob, string mask)
    {
        int lastStartIndex = bufferLength - currentAob.Size;
        if (lastStartIndex < initIndex)
        {
            return -1;
        }

        for (int i = initIndex; i <= lastStartIndex; i++)
        {
            for (int x = 0; x < currentAob.Size; x++)
            {
                if (currentAob[x] != buffer[i + x] && mask[x] != '?')
                {
                    goto end;
                }
            }
            return i;
            end:;
        }
        return -1;
    }

    private void ConvertPatternToAob(string inPatternString, out string mask, out Block<byte> currentAob)
    {
        string trimmed = inPatternString.Trim();

        mask = "";
        string[] partHex = trimmed.Split(' ');
        currentAob = new Block<byte>(partHex.Length);
        for (int i = 0; i < partHex.Length; ++i)
        {
            if (partHex[i].Contains('?'))
            {
                currentAob[i] = 0xCC;
                mask += '?';
            }
            else
            {
                currentAob[i] = Convert.ToByte(partHex[i], 16);
                mask += 'x';
            }
        }
    }

    private IEnumerable<(nint Address, int Size)> GetRegions()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            nint currentAddress = m_process.MainModule?.BaseAddress ?? 0;

            while (true)
            {
                if (VirtualQueryEx(m_processHandle, currentAddress, out MemoryBasicInformation64 region, Unsafe.SizeOf<MemoryBasicInformation64>()) == 0)
                {
                    if (Marshal.GetLastPInvokeError() == (int) SystemErrorCode.InvalidParameter)
                    {
                        break;
                    }

                    throw new Win32Exception();
                }

                if (region.State.HasFlag(AllocationType.Commit) && region.Protect != ProtectionType.NoAccess && !region.Protect.HasFlag(ProtectionType.Guard))
                {
                    yield return (currentAddress, (int) region.RegionSize);
                }

                currentAddress = (nint) region.BaseAddress + region.RegionSize;
            }
        }
        else
        {
            string path = $"/proc/{m_process.Id}/maps";
            foreach (string region in File.ReadLines(path))
            {
                string[] arr = region.Split(' ');
                int index = arr[0].IndexOf('-');
                nint start = nint.Parse(arr[0][..index], NumberStyles.HexNumber);
                nint end = nint.Parse(arr[0][(index + 1)..], NumberStyles.HexNumber);

                string perm = arr[1];
                if (perm[0] == '-' /*|| perm[1] == '-'*/ || perm[2] != 'x')
                {
                    continue;
                }

                yield return (start, (int)(end - start));
            }
        }
    }

    private void ReadMemory(nint inAddress, Block<byte> outData, out nint bytesRead)
    {
        if (!TryReadMemory(inAddress, outData, out bytesRead))
        {
            throw new Win32Exception();
        }
    }

    private bool TryReadMemory(nint inAddress, Block<byte> outData, out nint bytesRead)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            bool success = ReadProcessMemory(m_processHandle, inAddress, (nint)outData.Ptr, outData.Size, out bytesRead);
            if (!success && bytesRead == 0)
            {
                return false;
            }

            return true;
        }

        Iovec localIo = new() { iov_base = outData.Ptr, iov_len = (nuint)outData.Size };
        Iovec remoteIo = new() { iov_base = inAddress.ToPointer(), iov_len = (nuint)outData.Size };

        if ((bytesRead = process_vm_readv(m_process.Id, &localIo, 1, &remoteIo, 1, 0)) == -1)
        {
            return false;
        }

        return true;
    }

    private void ReadMemory(nint inAddress, Span<byte> outData, out nint bytesRead)
    {
        fixed (byte* ptr = outData)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                bool success = ReadProcessMemory(m_processHandle, inAddress, (nint)ptr, outData.Length, out bytesRead);
                if (!success && bytesRead == 0)
                {
                    throw new Win32Exception();
                }
            }
            else
            {
                Iovec localIo = new() { iov_base = ptr, iov_len = (nuint)outData.Length };
                Iovec remoteIo = new() { iov_base = inAddress.ToPointer(), iov_len = (nuint)outData.Length };

                if ((bytesRead = process_vm_readv(m_process.Id, &localIo, 1, &remoteIo, 1, 0)) == -1)
                {
                    throw new Exception();
                }
            }
        }
    }
}
