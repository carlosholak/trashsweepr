namespace TrashSweepr.Cleanup.Core.Models;

/// <summary>Nível de risco de um tweak. A ordem importa: valores maiores são mais arriscados.</summary>
public enum RiskLevel
{
    Safe = 0,
    Moderate = 1,
    Advanced = 2,
}

public enum TweakCategory
{
    Apps,
    Privacy,
    Services,
    Tasks,
    Interface,
    Performance,
}

public enum RegistryHive
{
    Hkcu,
    Hklm,
}

public enum RegistryOperation
{
    Set,
    DeleteValue,
    DeleteKey,
}

public enum RegistryValueType
{
    Dword,
    Qword,
    Sz,
    ExpandSz,
}

public enum ServiceStartType
{
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
}

public enum AppxOperation
{
    Remove,
    Reinstall,
}
