namespace Photon.Engine;

// Honest classification of a WMMR engine DLL as probed at runtime.
public enum EngineKind
{
    // DLL directory missing / LoadLibrary failed / export probe never ran.
    NotLoaded,

    // Loaded, but the export surface is a stub (COM quartet returning
    // CLASS_E_CLASSNOTAVAILABLE, or no-op telemetry aliases).
    EngineStub,

    // Loaded and a real/clean callable surface was exercised (init, COM
    // can-unload, or a real C API).
    EnginePresent,
}

// Per-DLL runtime status shown in the "WMMR Engines" panel.
public sealed class DllStatus
{
    public string Name { get; init; } = "";
    public string? FullPath { get; init; }
    public bool IsLoaded { get; set; }
    public int LoadErrorCode { get; set; }
    public string? LoadErrorText { get; set; }
    public int ExportsFound { get; set; }
    public int? LastHResult { get; set; }
    public EngineKind Kind { get; set; }
    public string Notes { get; set; } = "";

    public string StateLabel => Kind switch
    {
        EngineKind.EnginePresent => "engine present",
        EngineKind.EngineStub => "engine stub",
        _ => "not loaded",
    };

    public string HResultLabel =>
        LastHResult is int hr ? Hr.Describe(hr) : (IsLoaded ? "not called" : "—");
}
