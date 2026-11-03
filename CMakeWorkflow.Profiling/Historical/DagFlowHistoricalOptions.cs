using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

public sealed record DagFlowHistoricalOptions(
    string SourceDirectory,
    string OutputDirectory,
    int TimingTrials = 5,
    int StatTrials = 3,
    int Jobs = 4,
    bool Resume = false);
