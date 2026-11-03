using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record NativeBuildResult(
    CMakeTargetRef Target,
    string Verification,
    int? BackendExitCode,
    string? ObservationIndex,
    GenerationObservation? Generation = null);
