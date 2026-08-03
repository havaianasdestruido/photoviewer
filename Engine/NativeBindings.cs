using System.Runtime.InteropServices;
using System.Text;

namespace Photon.Engine;

// HRESULT helpers + canonical constants. Reused from the proven test suite
// (tests/mmr-gui/Native.cs) so every error path formats identically.
internal static class Hr
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int E_POINTER = unchecked((int)0x80004003);

    public static bool Is(int actual, int expected) =>
        unchecked((uint)actual) == unchecked((uint)expected);

    public static string Hex(int hr) => $"0x{unchecked((uint)hr) & 0xFFFFFFFF:X8}";

    public static string Name(int hr) => hr switch
    {
        S_OK => "S_OK",
        S_FALSE => "S_FALSE",
        E_NOTIMPL => "E_NOTIMPL",
        E_INVALIDARG => "E_INVALIDARG",
        E_ACCESSDENIED => "E_ACCESSDENIED",
        CLASS_E_CLASSNOTAVAILABLE => "CLASS_E_CLASSNOTAVAILABLE",
        E_UNEXPECTED => "E_UNEXPECTED",
        E_OUTOFMEMORY => "E_OUTOFMEMORY",
        E_POINTER => "E_POINTER",
        _ => Hex(hr),
    };

    // "S_OK (0x00000000)" style composite for status panels.
    public static string Describe(int hr) => $"{Name(hr)} ({Hex(hr)})";
}

// A single engine operation result: what was called, what came back.
internal sealed class EngineCallResult
{
    public string Dll { get; init; } = "";
    public string Op { get; init; } = "";
    public int HResult { get; init; } = Hr.E_UNEXPECTED;
    public string Detail { get; init; } = "";
    public override string ToString() => $"{Dll}.{Op}: {Hr.Describe(HResult)} {Detail}".TrimEnd();
}

// WLMediaProperties layout from src/WLMFReadWrite/WLMFReadWrite.h.
// WCHAR wszFileType[32]; then UINT32 x8, LONGLONG, GUID x2, BOOL x2.
[StructLayout(LayoutKind.Sequential)]
internal struct WLMediaProperties
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public ushort[] wszFileType;
    public uint uVideoWidth;
    public uint uVideoHeight;
    public uint uFrameRateNumerator;
    public uint uFrameRateDenominator;
    public uint uVideoBitrate;
    public uint uAudioBitrate;
    public uint uAudioSampleRate;
    public uint uAudioChannels;
    public long llDuration;
    public Guid guidVideoSubtype;
    public Guid guidAudioSubtype;
    public int bHasVideo;
    public int bHasAudio;
}

// All WMMR entrypoints we call are __stdcall. Explicit argtypes via
// UnmanagedFunctionPointer + explicit marshalling (LPWStr where the C++
// source shows WCHAR/LPCWSTR). Proven signatures are copied from
// tests/mmr-gui/{Native.cs,NativeProbe.cs}.
internal static class WmmrDelegates
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FnVoid0();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int FnHr0();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int FnGetClassObject(ref Guid rclsid, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int FnPropertyHandler(IntPtr pItem, uint dwAccessMode, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint FnGetOptInState();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate byte FnIsEnabled();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FnSqmSet(uint id, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr FnMfOpen([MarshalAs(UnmanagedType.LPWStr)] string path);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int FnMfGetProperties(IntPtr hReader, ref WLMediaProperties props);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int FnGetEnvironment(out IntPtr env);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FnFreeMemory(IntPtr p);
}

// Minimal LoadLibrary/GetProcAddress wrapper so the app can probe and call
// the WMMR surface dynamically. Mirrors the proven NativeProbe.cs.
internal static class DllProbe
{
    private static readonly Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);

    public static bool SetDllDirectory(string path) => SetDllDirectoryW(path);

    public static IntPtr Load(string dllFileName)
    {
        if (_handles.TryGetValue(dllFileName, out var h) && h != IntPtr.Zero)
            return h;
        var loaded = LoadLibraryW(dllFileName);
        _handles[dllFileName] = loaded;
        return loaded;
    }

    public static bool DllLoads(string dllFileName) => Load(dllFileName) != IntPtr.Zero;

    public static int LoadError(string dllFileName)
    {
        if (_handles.TryGetValue(dllFileName, out var h) && h != IntPtr.Zero)
            return 0;
        var loaded = LoadLibraryW(dllFileName);
        _handles[dllFileName] = loaded;
        return loaded == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
    }

    public static IntPtr GetExport(string dllFileName, string procName)
    {
        var h = Load(dllFileName);
        return h == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(h, procName);
    }

    public static bool ExportExists(string dllFileName, string procName) =>
        GetExport(dllFileName, procName) != IntPtr.Zero;

    public static T? GetFn<T>(string dllFileName, string procName) where T : Delegate
    {
        IntPtr p = GetExport(dllFileName, procName);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }
}
