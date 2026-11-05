#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public enum PackageManagerKind
{
    Auto,
    None,
    Apt,
    Pacman,
    Dnf,
    Brew,
    Winget
}
