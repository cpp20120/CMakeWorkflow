using CMakeWorkflow.Client.Experiments;
using System.Text.Json;
using CMakeWorkflow.Client;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Commands;

internal sealed record ExperimentHostPool(IReadOnlyList<ExperimentPoolHost> Hosts);
internal sealed record ExperimentPoolHost(string Name, ExperimentHost Connection);
internal static class ExperimentHostSelection
{
    internal static async Task<(ExperimentHost Host, ExperimentPlacement Placement)> SelectAsync(string poolPath,
        string? architecture, int minProcessors, bool managed, CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(poolPath);
        await using FileStream input = File.OpenRead(fullPath);
        if (input.Length > 64 * 1024)
        {
            throw new InvalidDataException("Experiment Host pool exceeds 64 KiB.");
        }

        ExperimentHostPool? pool = await JsonSerializer.DeserializeAsync(input, ExperimentJson.Default.ExperimentHostPool, ct);
        if (pool?.Hosts is not { Count: > 0 and <= 16 } hosts ||
            hosts.Any(host => host?.Name is not { Length: > 0 and <= 64 } ||
                              !host.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                              host.Connection is null) ||
            hosts.Select(host => host.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != hosts.Count)
        {
            throw new ArgumentException("Supply 1 to 16 Host entries with unique names and a connection.");
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        ExperimentPoolHost[] normalized = [.. hosts.Select(host => host with
        {
            Connection = Normalize(host.Connection, directory)
        })];
        (ExperimentPoolHost Host, ExperimentHostResources? Resources, string? Reason)[] inspected = await Task.WhenAll(
            normalized.Select(host => ProbeAsync(host, managed, ct)));
        ExperimentHost? selectedHost = null;
        ExperimentPlacement? placement = null;
        foreach ((ExperimentPoolHost host, ExperimentHostResources? resources, string? reason) in inspected)
        {
            if (resources is null)
            {
                await Console.Error.WriteLineAsync($"Host {host.Name} unavailable: {reason}");
                continue;
            }
            bool matches = (architecture is null || string.Equals(architecture, resources.Architecture, StringComparison.OrdinalIgnoreCase)) &&
                           resources.AvailableProcessors >= minProcessors;
            await Console.Error.WriteLineAsync(FormattableString.Invariant(
                $"Host {host.Name}: {resources.Architecture}, {resources.AvailableProcessors} CPUs, {(resources.ManagedCompilerVersion is { } version ? "managed Clang " + version : "preset compiler resolved at configure")} ({(matches ? "eligible" : "below requested capacity or architecture mismatch")})"));
            if (matches && (placement is null || resources.AvailableProcessors > placement.Resources.AvailableProcessors))
            {
                selectedHost = host.Connection;
                placement = new(host.Name, minProcessors, resources);
            }
        }
        return selectedHost is not null && placement is not null ? (selectedHost, placement)
            : throw new InvalidOperationException("No Host in the pool meets the experiment resource requirements.");
    }

    private static ExperimentHost Normalize(ExperimentHost host, string directory)
    {
        string? keyFile = host.KeyFile;
        if (string.IsNullOrWhiteSpace(host.Destination) || host.Destination.StartsWith('-') ||
            host.Destination.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("Host pool connections require an SSH destination without whitespace or control characters.");
        }
        bool validAuth = host.Username is null
            ? keyFile is null && host.HostKeySha256 is null && host.Port is 0 or 22
            : !string.IsNullOrWhiteSpace(host.Username) && !string.IsNullOrWhiteSpace(keyFile) &&
              !string.IsNullOrWhiteSpace(host.HostKeySha256) && host.Port is >= 0 and <= 65535;
        return validAuth
            ? host with
            {
                Workspace = ExperimentCommand.PosixPath(host.Workspace ?? ""),
                Endpoint = ExperimentCommand.PosixPath(host.Endpoint ?? ""),
                Executable = string.IsNullOrEmpty(host.Executable) ? "cmakeworkflow" : host.Executable,
                Port = host.Port == 0 ? 22 : host.Port,
                KeyFile = keyFile is null ? null : Path.GetFullPath(keyFile, directory)
            }
            : throw new ArgumentException("Supply username, keyFile and hostKeySha256 together for SSH.NET, or use an OpenSSH alias.");
    }

    private static async Task<(ExperimentPoolHost Host, ExperimentHostResources? Resources, string? Reason)> ProbeAsync(
        ExperimentPoolHost host, bool managed, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using CMakeWorkflowClient client = await host.Connection.ConnectAsync(deadline.Token);
            if (client.Host.WorkspaceDirectory != host.Connection.Workspace)
            {
                return (host, null, "Host workspace differs from the configured workspace.");
            }
            if (!client.Host.Capabilities.Contains("experiment/sourceChunk", StringComparer.Ordinal) ||
                !client.Host.Capabilities.Contains("experiment/stage", StringComparer.Ordinal))
            {
                return (host, null, "Source upload capability unavailable.");
            }
            ExperimentHostResources resources = await client.Experiments.InspectResourcesAsync(managed, deadline.Token);
            return resources.AvailableProcessors > 0 && (!managed || resources.ManagedToolchainFingerprint is { Length: > 0 })
                ? (host, resources, null) : (host, null, "Incomplete resource information.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (host, null, "Resource inspection timed out.");
        }
        catch (Exception error) when (CliErrors.IsExpected(error))
        {
            return (host, null, error.Message);
        }
    }
}
