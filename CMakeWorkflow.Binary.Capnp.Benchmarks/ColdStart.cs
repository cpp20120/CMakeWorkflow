using System.Diagnostics;
using System.Globalization;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Binary.Capnp.Benchmarks;

internal static class ColdStart
{
    internal static void Measure(string format, int size)
    {
        WorkflowDocument document = MatrixDocuments.Create(size);
        Stopwatch timer = Stopwatch.StartNew();
        int bytes = format == "json" ? MeasureJson(document) : MeasureBinary(document);
        timer.Stop();
        using Process self = Process.GetCurrentProcess();
        Console.WriteLine(FormattableString.Invariant(
            $"first-pipeline-ms={timer.Elapsed.TotalMilliseconds:F3} peak-rss-kib={self.PeakWorkingSet64 / 1024} bytes={bytes}"));
    }

    internal static async Task RunAsync()
    {
        foreach (int size in new[] { 4, 65, 512 })
        {
            foreach (string format in new[] { "json", "capnp" })
            {
                ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true };
                start.ArgumentList.Add(typeof(ColdStart).Assembly.Location);
                start.ArgumentList.Add("--first");
                start.ArgumentList.Add(format);
                start.ArgumentList.Add(size.ToString(CultureInfo.InvariantCulture));
                Stopwatch timer = Stopwatch.StartNew();
                using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start benchmark.");
                string output = await child.StandardOutput.ReadToEndAsync();
                await child.WaitForExitAsync();
                if (child.ExitCode != 0)
                {
                    throw new InvalidOperationException("Cold benchmark failed.");
                }

                Console.WriteLine(FormattableString.Invariant(
                    $"{size} {format} process-ms={timer.Elapsed.TotalMilliseconds:F3} {output.Trim()}"));
            }
        }
    }

    private static int MeasureJson(WorkflowDocument document)
    {
        byte[] bytes = WorkflowManifestBytes.Serialize(document);
        _ = ManifestBenchmarks.ReadJson(bytes);
        _ = System.Security.Cryptography.SHA256.HashData(bytes);
        return bytes.Length;
    }

    private static int MeasureBinary(WorkflowDocument document)
    {
        byte[] bytes = CapnpWire.Canonicalize(WorkflowEncoding.Encode(document));
        _ = WorkflowEncoding.Decode(bytes);
        _ = CapnpWire.HashCanonical(bytes);
        return CapnpWire.Frame(bytes).Length;
    }
}
