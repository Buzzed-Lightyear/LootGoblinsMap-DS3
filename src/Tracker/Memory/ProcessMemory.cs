using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SoulMemory.Memory;

namespace Tracker.Memory;

/// <summary>Thrown when Windows refuses the read-only open or a read with "access denied".</summary>
public sealed class GameAccessDeniedException : Exception
{
    public GameAccessDeniedException(string message) : base(message) { }
}

/// <summary>
/// A read-only view of another process. The handle is opened with the read and limited-query
/// rights and nothing else; this class has no method that can change the target.
/// The main module is located through the process environment block, which needs no
/// further rights.
/// </summary>
public sealed class ProcessMemory : IMemorySource, IDisposable
{
    private nint _handle;

    public int ProcessId { get; }
    public long MainModuleBase { get; }
    public int MainModuleSize { get; }
    public string ExePath { get; }

    /// <summary>Win32 error of the most recent failed read (0 if the last read worked).</summary>
    public int LastReadError { get; private set; }

    private ProcessMemory(int processId, nint handle)
    {
        ProcessId = processId;
        _handle = handle;

        ExePath = QueryExePath();

        int status = NativeMethods.NtQueryInformationProcess(_handle, 0, out var pbi,
            Marshal.SizeOf<NativeMethods.ProcessBasicInformation>(), out _);
        if (status != 0 || pbi.PebBaseAddress == 0)
            throw new InvalidOperationException($"Could not locate the process environment block (status 0x{status:X8}).");

        // PEB.ImageBaseAddress is at +0x10 in a 64-bit process (DS3 is 64-bit only).
        MainModuleBase = ReadInt64OrThrow(pbi.PebBaseAddress + 0x10, "image base");
        int peOffset = ReadInt32OrThrow(MainModuleBase + 0x3C, "PE header offset");
        // OptionalHeader starts at +0x18, SizeOfImage is at +0x38 inside it.
        MainModuleSize = ReadInt32OrThrow(MainModuleBase + peOffset + 0x18 + 0x38, "image size");
        if (MainModuleSize <= 0 || MainModuleSize > 1 << 30)
            throw new InvalidOperationException($"Implausible main module size {MainModuleSize}.");
    }

    /// <summary>Opens the process read-only. Throws <see cref="GameAccessDeniedException"/> on access denied.</summary>
    public static ProcessMemory Open(int processId)
    {
        nint handle = NativeMethods.OpenReadOnly(processId);
        if (handle == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ErrorAccessDenied)
                throw new GameAccessDeniedException($"Access denied opening process {processId} for reading.");
            throw new Win32Exception(error, $"Could not open process {processId} for reading.");
        }

        try
        {
            return new ProcessMemory(processId, handle);
        }
        catch
        {
            NativeMethods.CloseHandle(handle);
            throw;
        }
    }

    public bool HasExited =>
        _handle == 0 ||
        !NativeMethods.GetExitCodeProcess(_handle, out uint code) ||
        code != NativeMethods.StillActive;

    public bool TryRead(long address, Span<byte> buffer)
    {
        if (_handle == 0 || buffer.IsEmpty || address <= 0)
        {
            LastReadError = 0;
            return false;
        }

        bool ok = NativeMethods.ReadProcessMemory(_handle, (nint)address, ref MemoryMarshal.GetReference(buffer),
            (nuint)buffer.Length, out nuint read);
        if (ok && read == (nuint)buffer.Length)
        {
            LastReadError = 0;
            return true;
        }

        LastReadError = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>
    /// Copies the main module image. Read in chunks so that an unreadable page only blanks
    /// that page (zero-filled) instead of failing the whole copy.
    /// </summary>
    public byte[] ReadMainModule()
    {
        const int Chunk = 1 << 20;
        const int Page = 4096;
        var image = new byte[MainModuleSize];
        for (int offset = 0; offset < image.Length; offset += Chunk)
        {
            int length = Math.Min(Chunk, image.Length - offset);
            if (TryRead(MainModuleBase + offset, image.AsSpan(offset, length)))
                continue;
            if (LastReadError == NativeMethods.ErrorAccessDenied)
                throw new GameAccessDeniedException("Access denied reading the game's main module.");
            for (int p = offset; p < offset + length; p += Page)
            {
                var page = image.AsSpan(p, Math.Min(Page, offset + length - p));
                if (!TryRead(MainModuleBase + p, page))
                    page.Clear();
            }
        }
        return image;
    }

    private string QueryExePath()
    {
        var buffer = new char[1024];
        int size = buffer.Length;
        return NativeMethods.QueryFullProcessImageNameW(_handle, 0, buffer, ref size)
            ? new string(buffer, 0, size)
            : "";
    }

    private long ReadInt64OrThrow(long address, string what)
    {
        Span<byte> b = stackalloc byte[8];
        ThrowIfUnreadable(TryRead(address, b), what);
        return BinaryPrimitives.ReadInt64LittleEndian(b);
    }

    private int ReadInt32OrThrow(long address, string what)
    {
        Span<byte> b = stackalloc byte[4];
        ThrowIfUnreadable(TryRead(address, b), what);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    private void ThrowIfUnreadable(bool ok, string what)
    {
        if (ok)
            return;
        if (LastReadError == NativeMethods.ErrorAccessDenied)
            throw new GameAccessDeniedException($"Access denied reading the game's {what}.");
        throw new Win32Exception(LastReadError, $"Could not read the game's {what}.");
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = 0;
        }
    }
}
