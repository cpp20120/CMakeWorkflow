namespace CMakeWorkflow.CMake.Reporting;

/// <summary>Compiler-cache observations from one build attempt.</summary>
/// <remarks>Null metrics are unavailable. A shared-server sample can include activity outside the build.</remarks>
public sealed record CompilerCacheStatistics(int SchemaVersion, string Profile, string Provider, string Scope,
    long? Hits = null, long? Misses = null, long? RemoteReadHits = null, long? RemoteWrites = null,
    double? CompilerWorkSeconds = null, long? BytesRead = null, long? BytesWritten = null,
    double? CompileSeconds = null);
