using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record BuiltArtifact(CMakeTargetRef TargetRef, string ExecutablePath);
