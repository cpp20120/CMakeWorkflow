using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

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
