namespace CMakeWorkflow.Host.History;

public sealed record HistoryCompareRequest(string DatabasePath, string BaselineRunId, string CandidateRunId);
