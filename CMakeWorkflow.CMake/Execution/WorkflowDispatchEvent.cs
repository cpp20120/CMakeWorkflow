using System.Text.Json.Nodes;
using System.Text;

namespace CMakeWorkflow;

public sealed record WorkflowDispatchEvent(string Type, string Label, TimeSpan Elapsed);