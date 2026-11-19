using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Capture;

namespace CMakeWorkflow.Profiling.Backends;

internal static class WindowsPmcConfiguration
{
    private static readonly string[] Keywords = ["ProcessThread", "Loader", "PmcProfile"];
    internal static async Task<(string Profile, string Configuration)> PrepareAsync(
        IProfileProcess process, ProfileRequest request, string raw, CancellationToken ct)
    {
        string[] names = (request.PmcEvents ?? "TotalCycles,InstructionRetired")
            .Split(',', StringSplitOptions.TrimEntries);
        if (request.MemoryStallEvent is { } memory && !names.Contains(memory, StringComparer.OrdinalIgnoreCase))
        {
            names = [.. names, memory];
        }
        if (names.Length is < 1 or > 4 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length
            || names.Any(n => n.Length is < 1 or > 128 || n.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            || request.PmcInterval < 4096)
        {
            throw new ArgumentException("PMC capture requires 1–4 distinct published source names and an interval >= 4096.");
        }

        CommandResult discovery = await process.RunAsync("wpr", ["-pmcsources"],
            request.WorkingDirectory, TimeSpan.FromSeconds(30), null, ct);
        ProfileEnvironment.RequireSuccess(discovery, "wpr -pmcsources");
        // WPR columns: ID, Name, Interval, Min, Max (optional logger columns follow).
        Dictionary<string, (int Id, long Min, long Max)> available = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in discovery.Output.Split('\n'))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 5 && int.TryParse(fields[0], CultureInfo.InvariantCulture, out int id)
                && long.TryParse(fields[3], CultureInfo.InvariantCulture, out long min)
                && long.TryParse(fields[4], CultureInfo.InvariantCulture, out long max))
            {
                available.Add(fields[1], (id, min, max));
            }
        }

        HashSet<int> selectedIds = [];
        JsonArray sources = [];
        XElement sampled = new("SampledCounters");
        foreach (string name in names)
        {
            if (!available.TryGetValue(name, out (int Id, long Min, long Max) source))
            {
                throw new ArgumentException($"WPR does not publish PMC source '{name}'. Inspect wpr -pmcsources.");
            }
            if (!selectedIds.Add(source.Id))
            {
                throw new ArgumentException("Selected PMC names resolve to duplicate hardware source IDs.");
            }
            if (request.PmcInterval < source.Min || request.PmcInterval > source.Max)
            {
                throw new ArgumentException($"PMC source '{name}' requires an interval between {source.Min} and {source.Max}.");
            }
            sources.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["sourceId"] = source.Id,
                ["interval"] = request.PmcInterval,
                ["role"] = string.Equals(name, request.MemoryStallEvent, StringComparison.OrdinalIgnoreCase)
                    ? "memory-stalls" : "counter"
            });
            sampled.Add(new XElement("SampledCounter", new XAttribute("Value", name),
                new XAttribute("Interval", request.PmcInterval)));
        }

        XDocument profile = new(new XElement("WindowsPerformanceRecorder", new XAttribute("Version", "1.0"),
            new XElement("Profiles",
                new XElement("SystemCollector", new XAttribute("Id", "Collector"), new XAttribute("Name", "CMakeWorkflow PMC"),
                    new XElement("BufferSize", new XAttribute("Value", 1024)), new XElement("Buffers", new XAttribute("Value", 64))),
                new XElement("SystemProvider", new XAttribute("Id", "Provider"),
                    new XElement("Keywords", Keywords
                        .Select(k => new XElement("Keyword", new XAttribute("Value", k))))),
                new XElement("HardwareCounter", new XAttribute("Id", "PMC"), new XAttribute("Strict", "true"), sampled),
                new XElement("Profile", new XAttribute("Id", "CMakeWorkflowPMC.Verbose.File"),
                    new XAttribute("Name", "CMakeWorkflowPMC"), new XAttribute("Description", "Target PMC overflow samples"),
                    new XAttribute("LoggingMode", "File"), new XAttribute("DetailLevel", "Verbose"),
                    new XElement("Collectors", new XElement("SystemCollectorId", new XAttribute("Value", "Collector"),
                        new XElement("SystemProviderId", new XAttribute("Value", "Provider")),
                        new XElement("HardwareCounterId", new XAttribute("Value", "PMC"))))))));
        string profilePath = Path.Combine(raw, "pmc.wprp");
        await File.WriteAllTextAsync(profilePath, profile.ToString() + "\n", ct);
        await File.WriteAllTextAsync(Path.Combine(raw, "pmc-sources.txt"), discovery.Output, ct);
        string config = Path.Combine(raw, "pmc-config.json");
        await File.WriteAllTextAsync(config, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["method"] = "etw-pmc-overflow",
            ["sources"] = sources,
            ["profileSha256"] = await ProfileArtifactHash.Sha256Async(profilePath, ct)
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        return (Path.GetFullPath(profilePath) + "!CMakeWorkflowPMC", config);
    }
}
