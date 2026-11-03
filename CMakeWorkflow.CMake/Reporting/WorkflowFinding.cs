using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record WorkflowFinding(
    string Code,
    string Severity,
    string NodeId,
    string Message,
    string[] SuggestedProfiles);
