using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record ArtifactReference(
    string BuildNode,
    CMakeTargetRef Target,
    string Executable,
    string Sha256,
    string Materialization);
