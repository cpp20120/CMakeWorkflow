using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

// Domain-specific conversion of perf's period-weighted samples. This is not
// the general profiler's unweighted stack analysis.
public static partial class DagFlowHistoricalFlamegraph
{
    private sealed class Frame(string name)
    {
        internal string Name { get; } = name;
        internal long Self;
        internal SortedDictionary<string, Frame> Children { get; } = new(StringComparer.Ordinal);
        internal Frame Child(string key)
        {
            if (!Children.TryGetValue(key, out var found)) Children[key] = found = new Frame(key);
            return found;
        }
    }

    [GeneratedRegex(@"^(?<comm>.+?)\s+(?<pid>\d+)(?:/(?<tid>\d+))?\s+(?:\[\s*\d+\]\s+)?(?<time>\d+(?:\.\d+)?):\s+(?<period>\d+)\s+(?<event>[A-Za-z][A-Za-z0-9_.:/-]*):(?:\s+(?<inline>.*))?$")]
    private static partial Regex Header();
    [GeneratedRegex(@"^\s*([0-9a-fA-Fx]+)\s+(.+?)\s+\(([^()]*)\)\s*$")]
    private static partial Regex FrameLine();

    public static JsonObject Parse(TextReader text, CancellationToken ct)
    {
        var root = new Frame("all");
        var frames = new List<string>();
        string? comm = null;
        string? sampledSymbol = null;
        long period = 0;
        long samples = 0, missing = 0, rejected = 0;
        void Finish()
        {
            if (comm is null) return;
            var node = root.Child(comm);
            if (frames.Count == 0)
            {
                missing++;
                node = node.Child(sampledSymbol ?? "[missing callchain]");
            }
            else
                for (var i = frames.Count - 1; i >= 0; i--) node = node.Child(frames[i]);
            node.Self = checked(node.Self + period);
            samples++;
            comm = null; sampledSymbol = null; frames.Clear();
        }
        string? line;
        while ((line = text.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) { Finish(); continue; }
            if (line.StartsWith('#')) continue;
            var header = Header().Match(line);
            if (header.Success)
            {
                Finish();
                comm = header.Groups["comm"].Value.Trim();
                if (!long.TryParse(header.Groups["period"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out period) || period < 1)
                    throw new InvalidDataException("perf sample has no positive period.");
                var inline = FrameLine().Match(header.Groups["inline"].Value);
                if (inline.Success) sampledSymbol = Name(inline);
                continue;
            }
            var frame = FrameLine().Match(line);
            if (comm is not null && frame.Success) frames.Add(Name(frame));
            else rejected++;
        }
        Finish();
        if (samples == 0 || rejected != 0)
            throw new InvalidDataException($"Incomplete period-weighted perf script: samples={samples}, rejected={rejected}.");
        // Persist an indexed prefix tree, not nested { c: [...] } objects. A real
        // unwound call chain can contain thousands of frames; JSON depth must
        // not depend on the length of the captured call chain.
        var json = Flatten(root);
        json["metadata"] = new JsonObject
        {
            ["samples"] = samples, ["missing_callchain_samples"] = missing,
            ["weight"] = "sample.period", ["event"] = "cycles:u"
        };
        return json;
    }

    private static string Name(Match frame)
    {
        var symbol = frame.Groups[2].Value.Trim();
        if (symbol is not ("[unknown]" or "??") && !symbol.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return symbol;
        var dso = Path.GetFileName(frame.Groups[3].Value.Trim());
        return "[unknown in " + (string.IsNullOrEmpty(dso) ? "[unknown]" : dso) + "]";
    }

    public static async Task<JsonObject> DecodeAsync(string perfData, string output, CancellationToken ct)
    {
        var result = await CommandExecution.RunAsync("perf",
            ["script", "-i", Path.GetFullPath(perfData), "-F",
                "comm,pid,tid,time,period,event,ip,sym,dso"],
            Path.GetDirectoryName(Path.GetFullPath(perfData))!, TimeSpan.FromMinutes(5),
            new Dictionary<string, string?> { ["LC_ALL"] = "C" }, ct);
        if (result.TimedOut || result.ExitCode != 0)
            throw new IOException("perf script failed: " + result.Output[^Math.Min(result.Output.Length, 3000)..]);
        var json = Parse(new StringReader(result.Stdout ?? result.Output), ct);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, json.ToJsonString() + "\n", ct);
        return json;
    }

    // Stable pre-order serialization. Parent indices are always smaller than
    // their children; no recursion or loss of stack frames/period weights.
    private static JsonObject Flatten(Frame root)
    {
        var nodes = new JsonArray();
        var pending = new Stack<(Frame Frame, int Parent)>();
        pending.Push((root, -1));
        while (pending.Count > 0)
        {
            var (frame, parent) = pending.Pop();
            var index = nodes.Count;
            nodes.Add(new JsonObject
            {
                ["p"] = parent, ["n"] = frame.Name, ["v"] = frame.Self
            });
            // Pre-order, preserving the previous ordinal-sorted branch order.
            foreach (var child in frame.Children.Values.Reverse())
                pending.Push((child, index));
        }
        return new JsonObject { ["format"] = "flat-prefix-tree-v1", ["nodes"] = nodes };
    }

    internal readonly record struct WeightedFrame(int Parent, string Name, long Self);

    // Accept the old nested representation too: users have already persisted
    // valid perf measurements, and those files must stay renderable after upgrade.
    internal static IReadOnlyList<WeightedFrame> ReadFrames(JsonObject tree)
    {
        var frames = new List<WeightedFrame>();
        if (tree["format"] is not null)
        {
            if (tree["format"]!.GetValue<string>() != "flat-prefix-tree-v1")
                throw new InvalidDataException("Unknown flamegraph format.");
            var nodes = tree["nodes"]?.AsArray()
                ?? throw new InvalidDataException("Missing indexed flamegraph nodes.");
            foreach (var item in nodes)
            {
                var node = item?.AsObject()
                    ?? throw new InvalidDataException("Invalid indexed flamegraph node.");
                var parent = node["p"]?.GetValue<int>()
                    ?? throw new InvalidDataException("Missing parent index.");
                if (parent < -1 || (frames.Count == 0 ? parent != -1 : parent >= frames.Count || parent < 0))
                    throw new InvalidDataException("Invalid flamegraph parent index.");
                var name = node["n"]?.GetValue<string>()
                    ?? throw new InvalidDataException("Missing frame name.");
                var weight = node["v"]?.GetValue<long>()
                    ?? throw new InvalidDataException("Missing frame weight.");
                if (weight < 0) throw new InvalidDataException("Negative flamegraph weight.");
                frames.Add(new WeightedFrame(parent, name, weight));
            }
        }
        else
        {
            var pending = new Stack<(JsonObject Node, int Parent)>();
            pending.Push((tree, -1));
            while (pending.Count > 0)
            {
                var (node, parent) = pending.Pop();
                var name = node["n"]?.GetValue<string>()
                    ?? throw new InvalidDataException("Missing legacy frame name.");
                var weight = node["v"]?.GetValue<long>()
                    ?? throw new InvalidDataException("Missing legacy frame weight.");
                if (weight < 0) throw new InvalidDataException("Negative legacy flamegraph weight.");
                var index = frames.Count;
                frames.Add(new WeightedFrame(parent, name, weight));
                var children = node["c"]?.AsArray()
                    ?? throw new InvalidDataException("Missing legacy flamegraph children.");
                foreach (var child in children.Reverse())
                    pending.Push((child?.AsObject()
                        ?? throw new InvalidDataException("Invalid legacy flamegraph child."), index));
            }
        }
        if (frames.Count == 0) throw new InvalidDataException("Empty flamegraph.");
        return frames;
    }

    private sealed record DrawFrame(string Name, long X, int Depth, long Total);
    public static string Svg(JsonObject tree, string title)
    {
        var source = ReadFrames(tree);
        var totals = new long[source.Count];
        var positions = new long[source.Count];
        var offsets = new long[source.Count];
        var depths = new int[source.Count];
        for (var i = source.Count - 1; i >= 0; i--)
        {
            totals[i] = checked(totals[i] + source[i].Self);
            if (source[i].Parent >= 0)
                totals[source[i].Parent] = checked(totals[source[i].Parent] + totals[i]);
        }
        var count = totals[0];
        if (count <= 0) throw new InvalidDataException("No period-weighted samples.");
        var frames = new List<DrawFrame>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var parent = source[i].Parent;
            if (parent >= 0)
            {
                positions[i] = checked(positions[parent] + offsets[parent]);
                offsets[parent] = checked(offsets[parent] + totals[i]);
                depths[i] = checked(depths[parent] + 1);
            }
            frames.Add(new DrawFrame(source[i].Name, positions[i], depths[i], totals[i]));
        }
        var height = checked(110 + (depths.Max() + 1) * 19);
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1400\" height=\"")
            .Append(height).Append("\" viewBox=\"0 0 1400 ").Append(height)
            .Append("\"><style>text{font-family:monospace;font-size:11px;pointer-events:none}")
            .Append(".frame{cursor:pointer}.frame:hover rect{stroke:black;stroke-width:1}")
            .Append("</style><rect width=\"100%\" height=\"100%\" fill=\"#fafafa\"/>")
            .Append("<text x=\"10\" y=\"24\" style=\"font-size:16px\">")
            .Append(System.Net.WebUtility.HtmlEncode(title)).Append("</text>")
            .Append("<text x=\"10\" y=\"45\">Width = sampled cycles (sum of periods: ")
            .Append(count.ToString("N0", CultureInfo.InvariantCulture))
            .Append("); root at bottom. Click a frame to zoom.</text>")
            .Append("<g onclick=\"reset()\" style=\"cursor:pointer\"><text x=\"10\" y=\"72\">Reset zoom</text></g>")
            .Append("<g onclick=\"search()\" style=\"cursor:pointer\"><text x=\"115\" y=\"72\">Search frames</text></g>");
        foreach (var frame in frames)
        {
            var name = frame.Name;
            var y = height - 15 - (frame.Depth + 1) * 19;
            var x = 10 + (double)frame.X / count * 1380;
            var width = (double)frame.Total / count * 1380;
            var hue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                SHA256.HashData(Encoding.UTF8.GetBytes(name))) % 55;
            var label = width >= 14 ? name[..Math.Min(name.Length, Math.Max(0, (int)(width / 7) - 1))] : "";
            svg.Append("<g class=\"frame\" data-x=\"").Append(frame.X).Append("\" data-w=\"")
                .Append(frame.Total).Append("\" data-d=\"").Append(frame.Depth)
                .Append("\" data-name=\"").Append(System.Net.WebUtility.HtmlEncode(name))
                .Append("\" onclick=\"zoom(this)\"><title>")
                .Append(System.Net.WebUtility.HtmlEncode(name)).Append(" — ")
                .Append(frame.Total.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" sampled cycles</title><rect x=\"").Append(x.ToString("F3", CultureInfo.InvariantCulture))
                .Append("\" y=\"").Append(y).Append("\" width=\"")
                .Append(width.ToString("F3", CultureInfo.InvariantCulture)).Append("\" height=\"18\" fill=\"hsl(")
                .Append(hue).Append(",85%,70%)\"/><text x=\"")
                .Append((x + 3).ToString("F3", CultureInfo.InvariantCulture))
                .Append("\" y=\"").Append(y + 13).Append("\">")
                .Append(System.Net.WebUtility.HtmlEncode(label)).Append("</text></g>");
        }
        svg.Append("<script><![CDATA[const full=").Append(count)
            .Append(";function draw(x,w,d){document.querySelectorAll('.frame').forEach(g=>{const gx=+g.dataset.x,gw=+g.dataset.w,gd=+g.dataset.d;const a=gd<d&&gx<=x&&gx+gw>=x+w,c=gd>=d&&gx>=x&&gx+gw<=x+w;g.style.display=a||c?'':'none';if(!a&&!c)return;const px=a?10:10+(gx-x)/w*1380,pw=a?1380:gw/w*1380,r=g.querySelector('rect'),t=g.querySelector('text');r.setAttribute('x',px);r.setAttribute('width',pw);t.setAttribute('x',px+3);t.textContent=pw<14?'':g.dataset.name.slice(0,Math.max(0,Math.floor(pw/7)-1));});}function zoom(g){draw(+g.dataset.x,+g.dataset.w,+g.dataset.d)}function reset(){draw(0,full,0)}")
            .Append("function search(){const term=prompt('Highlight function name (empty to clear)');if(term===null)return;document.querySelectorAll('.frame').forEach(g=>{const hit=term&&g.dataset.name.toLowerCase().includes(term.toLowerCase()),r=g.querySelector('rect');r.style.stroke=hit?'#6000ee':'';r.style.strokeWidth=hit?'2':'';});}]]></script></svg>\n");
        return svg.ToString();
    }
}
