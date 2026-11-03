using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace CMakeWorkflow.Profiling;

public sealed record EtwAnalysisResult(
    string EventDump,
    string Summary,
    string Samples,
    int CpuSamples,
    int ContextSwitchesIn,
    int ReadyEvents);
