using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed record PlanDiagnostic(string Severity, string Message);
