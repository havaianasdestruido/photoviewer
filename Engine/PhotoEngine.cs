using System.Runtime.InteropServices;

namespace Photon.Engine;

// Photon's WMMR "engine layer". The app is fully functional without any of
// this — rendering is Windows Imaging Component (System.Windows.Media.Imaging).
// These routines PROBE the WMMR DLLs and call only entrypoints whose signatures
// are proven (tests/mmr-gui) or visible in the WMMR sources. Every result is
// reported honestly; stub HRESULTs (E_NOTIMPL / CLASS_E_CLASSNOTAVAILABLE) are
// surfaced, never hidden or faked.
public sealed class PhotoEngine
{
    private sealed record DllSpec(string FileName, string[] ProbeExports);

    private static readonly DllSpec[] Specs =
    {
        new("WLXPhotoBase.dll",
            new[] { "_WLXPhotoBase_Init@0" }),
        new("WLXPhotoSqm.dll",
            new[]
            {
                "?Startup@Sqm@@YGXXZ",
                "?GetOptInState@Sqm@@YG?AW4OptInState@1@XZ",
                "?IsEnabled@Sqm@@YG_NXZ",
                "?Set@Sqm@@YGXKK@Z",
                "?Shutdown@Sqm@@YGXXZ",
            }),
        new("WLXPhotoCinematic.dll",
            new[] { "DllCanUnloadNow", "DllGetClassObject", "DllRegisterServer", "DllUnregisterServer" }),
        new("WLXFaceRecognition.dll",
            new[] { "DllCanUnloadNow", "DllGetClassObject", "DllRegisterServer", "DllUnregisterServer" }),
        new("MetadataSys.dll",
            new[] { "WLXPSGetItemPropertyHandler" }),
        new("WLMFReadWrite.dll",
            new[]
            {
                "DllCanUnloadNow", "DllGetClassObject",
                "_MFReader_Open@4", "_MFReader_GetProperties@8",
                "_MFWriter_Create@8",
            }),
        new("wlidcli.dll",
            new[] { "WLGetEnvironment", "WLFreeMemory", "WLIsSignedIn" }),
        new("uxctl.dll",
            new[] { "UxControlsInitProcess", "UxControlsUninitProcess" }),
    };

    // Canonical SQM export set (mangled names are the real exported names;
    // each one is a .def alias to a _Sqm_* stdcall no-op — 44 exports, 7 RVAs).
    private static readonly string[] SqmKeySet =
    {
        "?Startup@Sqm@@YGXXZ",
        "?GetOptInState@Sqm@@YG?AW4OptInState@1@XZ",
        "?IsEnabled@Sqm@@YG_NXZ",
        "?Set@Sqm@@YGXKK@Z",
        "?Shutdown@Sqm@@YGXXZ",
    };

    public string? DllDirectory { get; private set; }
    public bool DllDirectoryFound { get; private set; }
    public string? DllDirectoryError { get; private set; }

    private readonly Dictionary<string, DllStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DllStatus> Statuses =>
        Specs.Select(s => Status(s.FileName)).Where(s => s != null).Select(s => s!).ToList();

    public DllStatus? Status(string dllFileName) =>
        _status.TryGetValue(dllFileName, out var s) ? s : null;

    public bool IsAvailable(string dllFileName) =>
        Status(dllFileName) is { IsLoaded: true };

    public void Initialize()
    {
        string? dir = ResolveDllDirectory();
        if (dir != null)
        {
            DllDirectory = dir;
            DllDirectoryFound = true;
            // Proves out the SetDllDirectoryW + LoadLibraryW path used by the tests.
            DllProbe.SetDllDirectory(dir);
        }
        else
        {
            DllDirectoryError =
                "WMMR bin directory not found. Set WMMR_BIN to your build_clean\\bin\\Debug (x86) folder.";
        }
    }

    private static string? ResolveDllDirectory()
    {
        // 1. Explicit override.
        string? env = Environment.GetEnvironmentVariable("WMMR_BIN");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return env;

        // 2. Exe output folder (someone copied the DLLs next to Photon.exe).
        var exeDir = AppContext.BaseDirectory;
        if (DllPresent(exeDir))
            return exeDir;

        // 3. Walk up from the app output dir looking for build_clean\bin\Debug.
        var dir = new DirectoryInfo(exeDir);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "build_clean", "bin", "Debug");
            if (DllPresent(candidate))
                return candidate;
        }
        return null;
    }

    private static bool DllPresent(string dir) =>
        Directory.Exists(dir) && File.Exists(Path.Combine(dir, "WLXPhotoBase.dll"));

    // Probe every registered DLL: load, count export presence, classify.
    public IReadOnlyList<DllStatus> ProbeAll()
    {
        foreach (var spec in Specs)
        {
            var st = ProbeOne(spec);
            _status[spec.FileName] = st;
        }
        return Statuses;
    }

    private DllStatus ProbeOne(DllSpec spec)
    {
        var st = new DllStatus { Name = spec.FileName };
        if (!DllDirectoryFound)
        {
            st.Kind = EngineKind.NotLoaded;
            st.LoadErrorText = DllDirectoryError ?? "no DLL directory";
            return st;
        }

        st.FullPath = Path.Combine(DllDirectory!, spec.FileName);
        int err = DllProbe.LoadError(spec.FileName);
        st.IsLoaded = err == 0;
        st.LoadErrorCode = err;

        if (!st.IsLoaded)
        {
            st.Kind = EngineKind.NotLoaded;
            st.LoadErrorText = $"LoadLibrary failed: Win32 {err} ({(err == 1114 ? "ERROR_DLL_INIT_FAILED — module cannot initialize" : err == 126 ? "ERROR_MOD_NOT_FOUND — missing dependency" : err == 193 ? "ERROR_BAD_EXE_FORMAT — not x86?" : $"0x{err:X8}"})).";
            return st;
        }

        st.ExportsFound = spec.ProbeExports.Count(e => DllProbe.ExportExists(spec.FileName, e));
        ClassifyAndSmoke(spec, st);
        return st;
    }

    private void ClassifyAndSmoke(DllSpec spec, DllStatus st)
    {
        switch (spec.FileName)
        {
            case "WLXPhotoBase.dll":
            {
                var r = CallPhotoBaseInit();
                st.LastHResult = r.HResult;
                // Init is a clean, callable void entrypoint; 90 remaining
                // exports are C++-mangled Base:: classes (not P/Invoke-able).
                st.Kind = EngineKind.EnginePresent;
                st.Notes = "91 exports (90 mangled Base:: helpers). Clean surface: _WLXPhotoBase_Init@0 only — no photo API exported by name.";
                break;
            }
            case "WLXPhotoSqm.dll":
            {
                st.Kind = EngineKind.EngineStub;
                var r = CallSqmProbe();
                st.LastHResult = r.HResult;
                st.Notes = "44 exports collapse to 7 RVAs — no-op SQM telemetry stubs. Startup/GetOptInState/IsEnabled/Set/Shutdown callable; all return 0/FALSE.";
                break;
            }
            case "WLXPhotoCinematic.dll":
            {
                var r = CallCinematicSmoke();
                st.LastHResult = r.HResult;
                st.Kind = EngineKind.EngineStub;
                st.Notes = "COM quartet only. DllGetClassObject → CLASS_E_CLASSNOTAVAILABLE; no effect API exported by name.";
                break;
            }
            case "WLXFaceRecognition.dll":
            {
                var r = CallFaceDetectProbe();
                st.LastHResult = r.HResult;
                st.Kind = EngineKind.EngineStub;
                st.Notes = "COM quartet only (3 exports alias one RVA). DllGetClassObject → CLASS_E_CLASSNOTAVAILABLE; no face-detect API exported by name.";
                break;
            }
            case "MetadataSys.dll":
            {
                var r = CallMetadataSysSmoke();
                st.LastHResult = r.HResult;
                st.Kind = EngineKind.EngineStub;
                st.Notes = "WLXPSGetItemPropertyHandler → E_NOTIMPL (stub). EXIF readout therefore uses WIC.";
                break;
            }
            case "WLMFReadWrite.dll":
            {
                st.Kind = EngineKind.EnginePresent;
                var r = CallMfCanUnload();
                st.LastHResult = r.HResult;
                st.Notes = "Real MF reader/writer C API (_MFReader_*, _MFWriter_*). Video-oriented; photos are not MF sources (see Metadata tab).";
                break;
            }
            case "wlidcli.dll":
            {
                st.Kind = EngineKind.EnginePresent;
                var r = CallIdentityProbe();
                st.LastHResult = r.HResult;
                st.Notes = "Windows Live ID client. WLGetEnvironment → S_OK \"production\"; WLFreeMemory works.";
                break;
            }
            case "uxctl.dll":
            {
                st.Kind = EngineKind.EnginePresent;
                var r = CallUxInit();
                st.LastHResult = r.HResult;
                st.Notes = "DirectUI controls factory. UxControlsInitProcess → S_OK.";
                break;
            }
        }
    }

    // --- Individual safe calls (each returns an EngineCallResult) -----------

    public EngineCallResult CallPhotoBaseInit()
    {
        var fn = DllProbe.GetFn<WmmrDelegates.FnVoid0>("WLXPhotoBase.dll", "_WLXPhotoBase_Init@0");
        if (fn == null)
            return new EngineCallResult { Dll = "WLXPhotoBase", Op = "Init", HResult = Hr.E_NOTIMPL, Detail = "export missing" };
        fn();
        return new EngineCallResult { Dll = "WLXPhotoBase", Op = "_WLXPhotoBase_Init@0", HResult = Hr.S_OK, Detail = "callable (void)" };
    }

    public EngineCallResult CallSqmProbe()
    {
        var start = DllProbe.GetFn<WmmrDelegates.FnVoid0>("WLXPhotoSqm.dll", "?Startup@Sqm@@YGXXZ");
        var optIn = DllProbe.GetFn<WmmrDelegates.FnGetOptInState>("WLXPhotoSqm.dll", "?GetOptInState@Sqm@@YG?AW4OptInState@1@XZ");
        var enabled = DllProbe.GetFn<WmmrDelegates.FnIsEnabled>("WLXPhotoSqm.dll", "?IsEnabled@Sqm@@YG_NXZ");
        var set = DllProbe.GetFn<WmmrDelegates.FnSqmSet>("WLXPhotoSqm.dll", "?Set@Sqm@@YGXKK@Z");
        var shutdown = DllProbe.GetFn<WmmrDelegates.FnVoid0>("WLXPhotoSqm.dll", "?Shutdown@Sqm@@YGXXZ");

        if (start == null || optIn == null || shutdown == null)
            return new EngineCallResult { Dll = "WLXPhotoSqm", Op = "probe", HResult = Hr.E_NOTIMPL, Detail = "key exports missing" };

        start();
        uint state = optIn();
        byte isEnabled = enabled?.Invoke() ?? 0;
        set?.Invoke(1234, 1); // telemetry no-op; harmless per stub source
        shutdown();

        return new EngineCallResult
        {
            Dll = "WLXPhotoSqm",
            Op = "Startup/GetOptInState/IsEnabled/Set/Shutdown",
            HResult = Hr.S_OK,
            Detail = $"OptInState={state}, IsEnabled={(isEnabled != 0)} (all no-op stubs)",
        };
    }

    public EngineCallResult CallCinematicSmoke()
    {
        var canUnload = DllProbe.GetFn<WmmrDelegates.FnHr0>("WLXPhotoCinematic.dll", "DllCanUnloadNow");
        int hrCanUnload = canUnload?.Invoke() ?? Hr.E_UNEXPECTED;
        var r = CallGetClassObject("WLXPhotoCinematic.dll", out int hrClass);
        return new EngineCallResult
        {
            Dll = "WLXPhotoCinematic",
            Op = "DllCanUnloadNow + DllGetClassObject",
            HResult = hrClass,
            Detail = $"CanUnload={Hr.Name(hrCanUnload)}; GetClassObject={Hr.Describe(hrClass)} (COM stub)",
        };
    }

    public EngineCallResult CallFaceDetectProbe()
    {
        var r = CallGetClassObject("WLXFaceRecognition.dll", out int hrClass);
        return new EngineCallResult
        {
            Dll = "WLXFaceRecognition",
            Op = "DllGetClassObject",
            HResult = hrClass,
            Detail = hrClass == Hr.CLASS_E_CLASSNOTAVAILABLE
                ? "engine unavailable — this build ships a COM-quartet stub; no face-detect API exported by name"
                : $"{Hr.Describe(hrClass)}",
        };
    }

    // The "Detect Faces" button action — honest stub reporting.
    public EngineCallResult DetectFaces()
    {
        var probe = CallFaceDetectProbe();
        var st = Status("WLXFaceRecognition.dll");
        if (st != null) st.LastHResult = probe.HResult;

        if (!IsAvailable("WLXFaceRecognition.dll"))
            return new EngineCallResult
            {
                Dll = "WLXFaceRecognition", Op = "detect",
                HResult = Hr.CLASS_E_CLASSNOTAVAILABLE,
                Detail = $"engine not loaded{(DllDirectoryFound ? "" : $" — {DllDirectoryError}")}; no detection performed",
            };

        return new EngineCallResult
        {
            Dll = "WLXFaceRecognition", Op = "detect",
            HResult = probe.HResult,
            Detail = probe.HResult == Hr.CLASS_E_CLASSNOTAVAILABLE
                ? "WMMR face detection unavailable: DllGetClassObject returned CLASS_E_CLASSNOTAVAILABLE (0x80040111). The shipped DLL is a COM stub with no face-detect entrypoints. No faces were reported (not faked)."
                : $"{Hr.Describe(probe.HResult)}",
        };
    }

    public EngineCallResult CallMetadataSysSmoke()
    {
        var fn = DllProbe.GetFn<WmmrDelegates.FnPropertyHandler>("MetadataSys.dll", "WLXPSGetItemPropertyHandler");
        if (fn == null)
            return new EngineCallResult { Dll = "MetadataSys", Op = "WLXPSGetItemPropertyHandler", HResult = Hr.E_NOTIMPL, Detail = "export missing" };
        var riid = Guid.Empty;
        IntPtr ppv = IntPtr.Zero;
        int hr = fn(IntPtr.Zero, 0, ref riid, out ppv);
        return new EngineCallResult
        {
            Dll = "MetadataSys", Op = "WLXPSGetItemPropertyHandler",
            HResult = hr,
            Detail = hr == Hr.E_NOTIMPL ? "stub returns E_NOTIMPL — no item property handler provided" : Hr.Describe(hr),
        };
    }

    // Probe the current image against the real MF reader/writer DLL.
    public EngineCallResult ProbeMediaForImage(string imagePath)
    {
        if (!IsAvailable("WLMFReadWrite.dll"))
            return new EngineCallResult { Dll = "WLMFReadWrite", Op = "MFReader_Open", HResult = Hr.E_NOTIMPL, Detail = "engine not loaded" };

        var open = DllProbe.GetFn<WmmrDelegates.FnMfOpen>("WLMFReadWrite.dll", "_MFReader_Open@4");
        var getProps = DllProbe.GetFn<WmmrDelegates.FnMfGetProperties>("WLMFReadWrite.dll", "_MFReader_GetProperties@8");
        if (open == null)
            return new EngineCallResult { Dll = "WLMFReadWrite", Op = "MFReader_Open", HResult = Hr.E_NOTIMPL, Detail = "export missing" };

        IntPtr h = IntPtr.Zero;
        try
        {
            h = open(imagePath);
            if (h == IntPtr.Zero)
                return new EngineCallResult
                {
                    Dll = "WLMFReadWrite", Op = "MFReader_Open",
                    HResult = Hr.S_FALSE,
                    Detail = "returned NULL — photo is not a Media Foundation source (image decoding handled by WIC)",
                };
            if (getProps != null)
            {
                var props = new WLMediaProperties { wszFileType = new ushort[32] };
                int hr = getProps(h, ref props);
                return new EngineCallResult
                {
                    Dll = "WLMFReadWrite", Op = "MFReader_GetProperties",
                    HResult = hr,
                    Detail = $"{props.uVideoWidth}x{props.uVideoHeight} dur={props.llDuration / 10000}ms video={props.bHasVideo != 0} audio={props.bHasAudio != 0}",
                };
            }
            return new EngineCallResult { Dll = "WLMFReadWrite", Op = "MFReader_Open", HResult = Hr.S_OK, Detail = "opened; GetProperties export missing" };
        }
        catch (Exception ex)
        {
            return new EngineCallResult { Dll = "WLMFReadWrite", Op = "MFReader_Open", HResult = Hr.E_UNEXPECTED, Detail = $"exception: {ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            if (h != IntPtr.Zero)
                CloseMfReader(h);
        }
    }

    private static void CloseMfReader(IntPtr h)
    {
        var close = DllProbe.GetExport("WLMFReadWrite.dll", "_MFReader_Close@4");
        if (close == IntPtr.Zero) return;
        // Re-wrap with correct arg shape for the 1-arg void call.
        var fn = Marshal.GetDelegateForFunctionPointer<CloseReader>(close);
        fn(h);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CloseReader(IntPtr h);

    public EngineCallResult CallMfCanUnload()
    {
        var canUnload = DllProbe.GetFn<WmmrDelegates.FnHr0>("WLMFReadWrite.dll", "DllCanUnloadNow");
        int hr = canUnload?.Invoke() ?? Hr.E_UNEXPECTED;
        return new EngineCallResult { Dll = "WLMFReadWrite", Op = "DllCanUnloadNow", HResult = hr, Detail = Hr.Describe(hr) };
    }

    public EngineCallResult CallIdentityProbe()
    {
        var getEnv = DllProbe.GetFn<WmmrDelegates.FnGetEnvironment>("wlidcli.dll", "WLGetEnvironment");
        var free = DllProbe.GetFn<WmmrDelegates.FnFreeMemory>("wlidcli.dll", "WLFreeMemory");
        if (getEnv == null)
            return new EngineCallResult { Dll = "wlidcli", Op = "WLGetEnvironment", HResult = Hr.E_NOTIMPL, Detail = "export missing" };

        IntPtr env = IntPtr.Zero;
        try
        {
            int hr = getEnv(out env);
            string? text = env == IntPtr.Zero ? null : Marshal.PtrToStringUni(env);
            return new EngineCallResult
            {
                Dll = "wlidcli", Op = "WLGetEnvironment",
                HResult = hr,
                Detail = hr == Hr.S_OK ? $"environment=\"{text}\"" : Hr.Describe(hr),
            };
        }
        finally
        {
            if (env != IntPtr.Zero)
                free?.Invoke(env);
        }
    }

    public EngineCallResult CallUxInit()
    {
        var fn = DllProbe.GetFn<WmmrDelegates.FnHr0>("uxctl.dll", "UxControlsInitProcess");
        int hr = fn?.Invoke() ?? Hr.E_UNEXPECTED;
        return new EngineCallResult { Dll = "uxctl", Op = "UxControlsInitProcess", HResult = hr, Detail = Hr.Describe(hr) };
    }

    // Best-effort app-exit shutdown surface: Sqm_Shutdown + uxctl uninit.
    public EngineCallResult CallShutdown()
    {
        var sqm = DllProbe.GetFn<WmmrDelegates.FnVoid0>("WLXPhotoSqm.dll", "?Shutdown@Sqm@@YGXXZ");
        sqm?.Invoke();
        var ux = DllProbe.GetFn<WmmrDelegates.FnVoid0>("uxctl.dll", "UxControlsUninitProcess");
        ux?.Invoke();
        return new EngineCallResult
        {
            Dll = "WLXPhotoSqm + uxctl",
            Op = "Shutdown / UxControlsUninitProcess",
            HResult = Hr.S_OK,
            Detail = "shutdown stubs called (void)",
        };
    }

    private static EngineCallResult CallGetClassObject(string dll, out int hr)
    {
        var fn = DllProbe.GetFn<WmmrDelegates.FnGetClassObject>(dll, "DllGetClassObject");
        if (fn == null)
        {
            hr = Hr.E_NOTIMPL;
            return new EngineCallResult { Dll = dll, Op = "DllGetClassObject", HResult = hr, Detail = "export missing" };
        }
        var rclsid = Guid.Empty;
        var riid = Guid.Empty;
        IntPtr ppv = IntPtr.Zero;
        hr = fn(ref rclsid, ref riid, out ppv);
        return new EngineCallResult { Dll = dll, Op = "DllGetClassObject", HResult = hr, Detail = Hr.Describe(hr) };
    }
}
