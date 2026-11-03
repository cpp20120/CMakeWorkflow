using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Profiling;

public sealed record ProfileReplayOutcome(int SchemaVersion, string RunDirectory, string AnalysisFile);
