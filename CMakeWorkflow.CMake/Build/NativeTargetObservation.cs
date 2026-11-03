using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record NativeTargetObservation(
    string Name,
    string Configuration,
    string Type,
    string? Executable,
    string Evidence);
