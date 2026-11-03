using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.IntegrationTests;

internal sealed record CommandResult(int ExitCode, string Output);
