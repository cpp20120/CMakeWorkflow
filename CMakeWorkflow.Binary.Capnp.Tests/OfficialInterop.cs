using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using Capnp.Managed;
using CMakeWorkflow.Contracts.Workflows;

internal static class OfficialInterop
{
    internal static async Task CheckAsync(string cli, string oracle, string directory, WorkflowDocument expected,
        byte[] managedCanonical)
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string schema = Path.Combine(fixtures, "workflow.capnp");
        string future = Path.Combine(fixtures, "future.capnp");
        string text = await File.ReadAllTextAsync(Path.Combine(fixtures, "fixture.capnp.txt"));
        byte[] original = await RunAsync(cli, Encoding.UTF8.GetBytes(text), "encode", schema, "Workflow");
        byte[] oracleBytes = await CanonicalizeAsync(oracle, directory, original, "canonicalize");
        Check(oracleBytes.AsSpan().SequenceEqual(managedCanonical), "Official C++ did not produce the same bytes.");
        Check(CapnpWire.Canonicalize(CapnpWire.Unframe(original)).AsSpan().SequenceEqual(managedCanonical),
            "The managed canonicalizer disagrees on the reference-encoded input.");
        EqualDocument(expected, WorkflowEncoding.Decode(CapnpWire.Unframe(original)));

        byte[] resegmented = await CanonicalizeAsync(oracle, directory, original, "resegment");
        VerifyResegmented(resegmented, expected, managedCanonical);

        byte[] managedFrame = CapnpWire.Frame(managedCanonical);
        byte[] verified = await CanonicalizeAsync(oracle, directory, managedFrame, "verify");
        Check(verified.AsSpan().SequenceEqual(managedCanonical), "Official C++ changed a verified canonical message.");
        byte[] officialText = await RunAsync(cli, managedFrame, "decode", schema, "Workflow");
        byte[] reencoded = await RunAsync(cli, officialText, "encode", schema, "Workflow");
        EqualDocument(expected, WorkflowEncoding.Decode(CapnpWire.Unframe(reencoded)));

        byte[] futureDefault = await RunAsync(cli, Encoding.UTF8.GetBytes(text), "encode", future, "FutureWorkflow");
        Check(CapnpWire.Canonicalize(CapnpWire.Unframe(futureDefault)).AsSpan().SequenceEqual(managedCanonical),
            "Default-valued future data and pointer fields changed canonical identity.");

        string extendedText = text.Replace("schemaVersion = 2,", "schemaVersion = 2, revision = 9, note = \"future\",",
                StringComparison.Ordinal)
            .Replace("id = \"debug\", buildDirectory", "id = \"debug\", extra = \"extra context\", buildDirectory",
                StringComparison.Ordinal);
        byte[] futurePopulated = await RunAsync(cli, Encoding.UTF8.GetBytes(extendedText), "encode", future, "FutureWorkflow");
        byte[] canonicalFuture = CapnpWire.Canonicalize(CapnpWire.Unframe(futurePopulated));
        byte[] oracleFuture = await CanonicalizeAsync(oracle, directory, CapnpWire.Frame(canonicalFuture), "verify");
        EqualDocument(expected, WorkflowEncoding.Decode(canonicalFuture));
        Check(!canonicalFuture.AsSpan().SequenceEqual(managedCanonical)
              && canonicalFuture.AsSpan().SequenceEqual(oracleFuture), "Unknown fields were lost in canonicalization.");
        string extendedOutput = Encoding.UTF8.GetString(await RunAsync(cli, CapnpWire.Frame(canonicalFuture),
            "decode", future, "FutureWorkflow"));
        Check(extendedOutput.Contains("extra context", StringComparison.Ordinal)
              && extendedOutput.Contains("revision = 9", StringComparison.Ordinal)
              && extendedOutput.Contains("note = \"future\"", StringComparison.Ordinal), "Future fields did not survive.");
        string oldOutput = Encoding.UTF8.GetString(await RunAsync(cli, managedFrame, "decode", future, "FutureWorkflow"));
        Check(oldOutput.Contains("revision = 0", StringComparison.Ordinal), "Future reader did not default old data.");
    }

    private static void VerifyResegmented(byte[] frame, WorkflowDocument expected, byte[] managedCanonical)
    {
        MessageReader segmented = FrameCodec.Read(frame);
        Check(segmented.SegmentCount > 1, "The official workflow fixture was not resegmented.");
        EqualDocument(expected, WorkflowEncoding.Decode(segmented));
        Check(CanonicalMessage.CreateSegment(segmented).AsSpan().SequenceEqual(managedCanonical),
            "Resegmented workflow canonical identity changed.");
    }

    private static async Task<byte[]> CanonicalizeAsync(string oracle, string directory, byte[] frame, string mode)
    {
        string input = Path.Combine(directory, "interop-frame.bin");
        string output = Path.Combine(directory, "interop-canonical.bin");
        await File.WriteAllBytesAsync(input, frame);
        _ = await RunAsync(oracle, [], mode, input, output);
        return await File.ReadAllBytesAsync(output);
    }

    private static async Task<byte[]> RunAsync(string executable, byte[] input, params string[] arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start oracle.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using CancellationTokenRegistration terminate = timeout.Token.Register(() =>
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        });
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        Task<byte[]> read = ReadOutputAsync(process.StandardOutput.BaseStream, timeout.Token);
        await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
        process.StandardInput.Close();
        await Task.WhenAll(read, process.WaitForExitAsync(timeout.Token));
        string errorText = await error;
        Check(process.ExitCode == 0, $"{Path.GetFileName(executable)} returned {process.ExitCode}. {errorText}");
        return await read;
    }

    private static async Task<byte[]> ReadOutputAsync(Stream source, CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        await source.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static void EqualDocument(WorkflowDocument expected, WorkflowDocument actual) =>
        Check(JsonSerializer.Serialize(expected, WorkflowDefinitionJson.Default.WorkflowDocument)
              == JsonSerializer.Serialize(actual, WorkflowDefinitionJson.Default.WorkflowDocument),
            "Official and managed decoders disagree on workflow content.");

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
