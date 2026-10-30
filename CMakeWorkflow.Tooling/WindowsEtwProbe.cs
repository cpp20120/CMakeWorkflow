using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling;

public sealed record EtwProbeResult(string Capability, string Status, int? Win32Error, string Diagnostic);

public interface IWindowsEtwProbe
{
    EtwProbeResult Probe(string capability, CancellationToken cancellationToken);
}

/// <summary>Explicit, short-lived ETW session admission probe; never stops sessions it does not own.</summary>
public sealed class NativeWindowsEtwProbe : IWindowsEtwProbe
{
    private const uint TracedGuid = 0x00020000;
    private const uint RealTime = 0x00000100;
    private const uint SystemLogger = 0x02000000;
    private const uint Profile = 0x01000000;
    private const uint ContextSwitch = 0x00000010;
    private const uint Stop = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Wnode
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong Timestamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Properties
    {
        public Wnode Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize;
        public uint LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten;
        public uint LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint StartTrace(out ulong session, string name, IntPtr properties);
    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTrace(ulong session, string? name, IntPtr properties, uint controlCode);

    public EtwProbeResult Probe(string capability, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new(capability, "unsupported", null, "ETW requires Windows.");
        if (capability is not ("user" or "kernel.cpu" or "kernel.scheduler"))
            throw new ArgumentException("Unknown ETW capability.", nameof(capability));
        cancellationToken.ThrowIfCancellationRequested();
        var kernel = capability != "user";
        var name = "CMakeWorkflow-Doctor-" + Guid.NewGuid().ToString("N");
        var nameBytes = (name.Length + 1) * sizeof(char);
        var structSize = Marshal.SizeOf<Properties>();
        var size = checked(structSize + nameBytes);
        var buffer = Marshal.AllocHGlobal(size);
        ulong handle = 0;
        bool started = false;
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            var properties = new Properties
            {
                Wnode = new Wnode
                {
                    BufferSize = (uint)size,
                    Guid = Guid.NewGuid(),
                    ClientContext = 1,
                    Flags = TracedGuid
                },
                BufferSize = 64,
                MinimumBuffers = 2,
                MaximumBuffers = 4,
                LogFileMode = RealTime | (kernel ? SystemLogger : 0u),
                EnableFlags = capability switch
                {
                    "kernel.cpu" => Profile,
                    "kernel.scheduler" => ContextSwitch,
                    _ => 0u
                },
                LoggerNameOffset = (uint)structSize
            };
            Marshal.StructureToPtr(properties, buffer, false);
            var rc = StartTrace(out handle, name, buffer);
            if (rc != 0) return new(capability, Classify(rc), (int)rc, new Win32Exception((int)rc).Message);
            started = true;
            // Admission only: this proves that the OS accepted the requested session settings,
            // not that a consumer can retrieve events or that providers emitted samples.
            return new(capability, "available", null, "ETW accepted the trace session; event delivery not verified.");
        }
        finally
        {
            if (started)
            {
                // Never stop by global name alone; use only the handle acquired by this invocation.
                var stopCode = ControlTrace(handle, null, buffer, Stop);
                if (stopCode != 0)
                    throw new InvalidOperationException($"ETW probe session cleanup failed (Win32 {stopCode}); session {name} may remain active.");
            }
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string Classify(uint code) => code switch
    {
        5 or 1314 => "denied", // ERROR_ACCESS_DENIED / ERROR_PRIVILEGE_NOT_HELD
        183 or 1450 or 1455 or 8 => "busy", // collision / resource limits
        50 or 87 or 1 => "unsupported", // unsupported mode/parameters
        _ => "unverified"
    };
}
