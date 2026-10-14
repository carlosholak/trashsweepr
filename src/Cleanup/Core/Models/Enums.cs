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

/// <summary>
/// Programas que o trashsweepr sabe desinstalar pelo desinstalador oficial de cada um. Lista fechada: o JSON só aceita
/// "edge" e "onedrive" (veja <see cref="ManagedPrograms"/>), e cada par (programa, operação) tem um script fixo.
/// </summary>
public enum ManagedProgram
{
    Edge,
    OneDrive,
}

public enum ProgramOperation
{
    Uninstall,
    Reinstall,
}
