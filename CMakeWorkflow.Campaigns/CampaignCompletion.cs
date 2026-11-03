using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Campaigns;

internal sealed record CampaignCompletion(
    int SchemaVersion,
    string Campaign,
    string Report,
    string ReportSha256,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Status);
