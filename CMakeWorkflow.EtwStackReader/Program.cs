using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

if (args is not [string tracePath, string outputPath, string pidText]
    || !int.TryParse(pidText, CultureInfo.InvariantCulture, out int processId))
{
    Console.Error.WriteLine("Usage: CMakeWorkflow.EtwStackReader TRACE.etl OUTPUT.xml PROCESS_ID");
    return 2;
}

string indexPath = TraceLog.CreateFromEventTraceLogFile(tracePath, outputPath + ".etlx");
using TraceLog log = new(indexPath);
using XmlWriter writer = XmlWriter.Create(outputPath);
writer.WriteStartElement("Events");
foreach (SampledProfileTraceData sample in log.Events.ByEventType<SampledProfileTraceData>())
{
    if (sample.ProcessID != processId)
    {
        continue;
    }

    List<string> frames = [];
    for (TraceCallStack? frame = sample.CallStack(); frame is not null; frame = frame.Caller)
    {
        frames.Add(FormattableString.Invariant($"0x{frame.CodeAddress.Address:x}"));
    }

    XElement record = new("Event",
        new XElement("System",
            new XElement("TimeCreated", new XAttribute("SystemTime", sample.TimeStamp.ToUniversalTime())),
            new XElement("Execution",
                new XAttribute("ProcessID", sample.ProcessID),
                new XAttribute("ThreadID", sample.ThreadID),
                new XAttribute("ProcessorID", sample.ProcessorNumber))),
        new XElement("RenderingInfo", new XElement("EventName", "SampledProfile")),
        new XElement("EventData", new XElement("Data", new XAttribute("Name", "InstructionPointer"),
            FormattableString.Invariant($"0x{sample.InstructionPointer:x}"))));
    if (frames.Count > 0)
    {
        record.Add(new XElement("StackTrace", string.Join(';', frames)));
    }

    record.WriteTo(writer);
}

writer.WriteEndElement();
return 0;
