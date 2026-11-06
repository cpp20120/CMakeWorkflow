// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
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
