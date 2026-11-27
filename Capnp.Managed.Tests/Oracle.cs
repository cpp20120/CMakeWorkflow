using System.Diagnostics;

namespace Capnp.Managed.Tests;

internal delegate void CheckFixture(MessageReader reader);

internal static class Oracle
{
    internal static void Run(string executable, string directory, byte[] managed, CheckFixture inspect)
    {
        string framePath = Path.Combine(directory, "interop.frame");
        string rawPath = Path.Combine(directory, "interop.raw");
        foreach (string mode in new[] { "single", "far", "double" })
        {
            Require(executable, "generate", mode, framePath);
            byte[] frame = File.ReadAllBytes(framePath);
            MessageReader reference = FrameCodec.Read(frame);
            PointerKind root = reference.Root.Kind;
            PointerKind blob = reference.Root.AsStruct().GetPointer(5).Kind;
            bool shape = mode switch
            {
                "single" => reference.SegmentCount == 1,
                "far" => reference.SegmentCount > 1 && root == PointerKind.Far,
                "double" => reference.SegmentCount > 1 && blob == PointerKind.DoubleFar,
                _ => false
            };
            inspect(reference);
            Require(executable, "canonicalize", framePath, rawPath);
            byte[] cpp = File.ReadAllBytes(rawPath);
            if (!shape || !managed.AsSpan().SequenceEqual(cpp)
                || !cpp.AsSpan().SequenceEqual(CanonicalMessage.CreateSegment(reference)))
            {
                throw new InvalidOperationException("Official interoperability mismatch in " + mode);
            }

            Console.WriteLine($"Official {mode} layout passed ({reference.SegmentCount} segments, {frame.Length} bytes).");
        }

        File.WriteAllBytes(framePath, FrameCodec.Write(new MessageReader(managed)));
        Require(executable, "verify", framePath, rawPath);
        if (!managed.AsSpan().SequenceEqual(File.ReadAllBytes(rawPath)))
        {
            throw new InvalidOperationException("Official canonical reader changed the managed canonical bytes.");
        }
    }

    private static void Require(string executable, params string[] arguments)
    {
        ProcessStartInfo start = new(executable);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start oracle.");
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Official Cap’n Proto oracle timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Official Cap’n Proto oracle returned {process.ExitCode}.");
        }
    }
}
