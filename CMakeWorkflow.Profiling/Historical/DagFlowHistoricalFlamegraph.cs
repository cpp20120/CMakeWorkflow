using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CMakeWorkflow.Core;

namespace CMakeWorkflow.Profiling.Historical;

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
            if (!Children.TryGetValue(key, out Frame? found))
            {
                Children[key] = found = new Frame(key);
            }

            return found;
        }
    }

    [GeneratedRegex(
        @"^(?<comm>.+?)\s+(?<pid>\d+)(?:/(?<tid>\d+))?\s+(?:\[\s*\d+\]\s+)?(?<time>\d+(?:\.\d+)?):\s+(?<period>\d+)\s+(?<event>[A-Za-z][A-Za-z0-9_.:/-]*):(?:\s+(?<inline>.*))?$")]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s*([0-9a-fA-Fx]+)\s+(.+?)\s+\(([^()]*)\)\s*$")]
    private static partial Regex FrameLine();

    public static JsonObject Parse(TextReader text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        Frame root = new("all");
        List<string> frames = [];
        string? comm = null;
        string? sampledSymbol = null;
        long period = 0;
        long samples = 0, missing = 0, rejected = 0;

        void Finish()
        {
            if (comm is null)
            {
                return;
            }

            Frame node = root.Child(comm);
            if (frames.Count == 0)
            {
                missing++;
                node = node.Child(sampledSymbol ?? "[missing callchain]");
            }
            else
            {
                for (int i = frames.Count - 1; i >= 0; i--)
                {
                    node = node.Child(frames[i]);
                }
            }

            node.Self = checked(node.Self + period);
            samples++;
            comm = null;
            sampledSymbol = null;
            frames.Clear();
        }

        string? line;
        while ((line = text.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                Finish();
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            Match header = Header().Match(line);
            if (header.Success)
            {
                Finish();
                comm = header.Groups["comm"].Value.Trim();
                if (!long.TryParse(header.Groups["period"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out period) || period < 1)
                {
                    throw new InvalidDataException("perf sample has no positive period.");
                }

                Match inline = FrameLine().Match(header.Groups["inline"].Value);
                if (inline.Success)
                {
                    sampledSymbol = Name(inline);
                }

                continue;
            }

            Match frame = FrameLine().Match(line);
            if (comm is not null && frame.Success)
            {
                frames.Add(Name(frame));
            }
            else
            {
                rejected++;
            }
        }

        Finish();
        if (samples == 0 || rejected != 0)
        {
            throw new InvalidDataException(
                $"Incomplete period-weighted perf script: samples={samples}, rejected={rejected}.");
        }
        // Persist an indexed prefix tree, not nested { c: [...] } objects. A real
        // unwound call chain can contain thousands of frames; JSON depth must
        // not depend on the length of the captured call chain.
        JsonObject json = Flatten(root);
        json["metadata"] = new JsonObject
        {
            ["samples"] = samples,
            ["missing_callchain_samples"] = missing,
            ["weight"] = "sample.period",
            ["event"] = "cycles:u"
        };
        return json;
    }

    private static string Name(Match frame)
    {
        string symbol = frame.Groups[2].Value.Trim();
        if (symbol is not ("[unknown]" or "??") && !symbol.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return symbol;
        }

        string dso = Path.GetFileName(frame.Groups[3].Value.Trim());
        return "[unknown in " + (string.IsNullOrEmpty(dso) ? "[unknown]" : dso) + "]";
    }

    public static async Task<JsonObject> DecodeAsync(string perfData, string output, CancellationToken ct)
    {
        CommandResult result = await CommandExecution.RunAsync("perf",
            [
                "script", "-i", Path.GetFullPath(perfData), "-F",
                "comm,pid,tid,time,period,event,ip,sym,dso"
            ],
            Path.GetDirectoryName(Path.GetFullPath(perfData))!, TimeSpan.FromMinutes(5),
            new Dictionary<string, string?> { ["LC_ALL"] = "C" }, ct);
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new IOException("perf script failed: " + result.Output[^Math.Min(result.Output.Length, 3000)..]);
        }

        JsonObject json = Parse(new StringReader(result.Stdout ?? result.Output), ct);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, json.ToJsonString() + "\n", ct);
        return json;
    }

    // Stable pre-order serialization. Parent indices are always smaller than
    // their children; no recursion or loss of stack frames/period weights.
    private static JsonObject Flatten(Frame root)
    {
        JsonArray nodes = [];
        Stack<(Frame Frame, int Parent)> pending = new();
        pending.Push((root, -1));
        while (pending.Count > 0)
        {
            (Frame? frame, int parent) = pending.Pop();
            int index = nodes.Count;
            nodes.Add((JsonNode?)new JsonObject
            {
                ["p"] = parent,
                ["n"] = frame.Name,
                ["v"] = frame.Self
            });
            // Pre-order, preserving the previous ordinal-sorted branch order.
            foreach (Frame? child in frame.Children.Values.Reverse())
            {
                pending.Push((child, index));
            }
        }

        return new JsonObject { ["format"] = "flat-prefix-tree-v1", ["nodes"] = nodes };
    }

    internal readonly record struct WeightedFrame(int Parent, string Name, long Self);

    // Accept the old nested representation too: users have already persisted
    // valid perf measurements, and those files must stay renderable after upgrade.
    internal static IReadOnlyList<WeightedFrame> ReadFrames(JsonObject tree)
    {
        List<WeightedFrame> frames = [];
        if (tree["format"] is not null)
        {
            if (tree["format"]!.GetValue<string>() != "flat-prefix-tree-v1")
            {
                throw new InvalidDataException("Unknown flamegraph format.");
            }

            JsonArray nodes = tree["nodes"]?.AsArray()
                        ?? throw new InvalidDataException("Missing indexed flamegraph nodes.");
            foreach (JsonNode? item in nodes)
            {
                JsonObject node = item?.AsObject()
                           ?? throw new InvalidDataException("Invalid indexed flamegraph node.");
                int parent = node["p"]?.GetValue<int>()
                             ?? throw new InvalidDataException("Missing parent index.");
                if (parent < -1 || (frames.Count == 0 ? parent != -1 : parent >= frames.Count || parent < 0))
                {
                    throw new InvalidDataException("Invalid flamegraph parent index.");
                }

                string name = node["n"]?.GetValue<string>()
                           ?? throw new InvalidDataException("Missing frame name.");
                long weight = node["v"]?.GetValue<long>()
                             ?? throw new InvalidDataException("Missing frame weight.");
                if (weight < 0)
                {
                    throw new InvalidDataException("Negative flamegraph weight.");
                }

                frames.Add(new WeightedFrame(parent, name, weight));
            }
        }
        else
        {
            Stack<(JsonObject Node, int Parent)> pending = new();
            pending.Push((tree, -1));
            while (pending.Count > 0)
            {
                (JsonObject? node, int parent) = pending.Pop();
                string name = node["n"]?.GetValue<string>()
                           ?? throw new InvalidDataException("Missing legacy frame name.");
                long weight = node["v"]?.GetValue<long>()
                             ?? throw new InvalidDataException("Missing legacy frame weight.");
                if (weight < 0)
                {
                    throw new InvalidDataException("Negative legacy flamegraph weight.");
                }

                int index = frames.Count;
                frames.Add(new WeightedFrame(parent, name, weight));
                JsonArray children = node["c"]?.AsArray()
                               ?? throw new InvalidDataException("Missing legacy flamegraph children.");
                foreach (JsonNode? child in children.Reverse())
                {
                    pending.Push((child?.AsObject()
                                  ?? throw new InvalidDataException("Invalid legacy flamegraph child."), index));
                }
            }
        }

        return frames.Count == 0 ? throw new InvalidDataException("Empty flamegraph.") : (IReadOnlyList<WeightedFrame>)frames;
    }

    private sealed record DrawFrame(string Name, long X, int Depth, long Total);

    public static string Svg(JsonObject tree, string title)
    {
        ArgumentNullException.ThrowIfNull(tree);
        IReadOnlyList<WeightedFrame> source = ReadFrames(tree);
        long[] totals = new long[source.Count];
        long[] positions = new long[source.Count];
        long[] offsets = new long[source.Count];
        int[] depths = new int[source.Count];
        for (int i = source.Count - 1; i >= 0; i--)
        {
            totals[i] = checked(totals[i] + source[i].Self);
            if (source[i].Parent >= 0)
            {
                totals[source[i].Parent] = checked(totals[source[i].Parent] + totals[i]);
            }
        }

        long count = totals[0];
        if (count <= 0)
        {
            throw new InvalidDataException("No period-weighted samples.");
        }

        List<DrawFrame> frames = new(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            int parent = source[i].Parent;
            if (parent >= 0)
            {
                positions[i] = checked(positions[parent] + offsets[parent]);
                offsets[parent] = checked(offsets[parent] + totals[i]);
                depths[i] = checked(depths[parent] + 1);
            }

            frames.Add(new DrawFrame(source[i].Name, positions[i], depths[i], totals[i]));
        }

        int height = checked(110 + ((depths.Max() + 1) * 19));
        StringBuilder svg = new();
        _ = svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1400\" height=\"")
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
            .Append(
                "<g onclick=\"search()\" style=\"cursor:pointer\"><text x=\"115\" y=\"72\">Search frames</text></g>");
        foreach (DrawFrame frame in frames)
        {
            string name = frame.Name;
            int y = height - 15 - ((frame.Depth + 1) * 19);
            double x = 10 + ((double)frame.X / count * 1380);
            double width = (double)frame.Total / count * 1380;
            uint hue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                SHA256.HashData(Encoding.UTF8.GetBytes(name))) % 55;
            string label = width >= 14 ? name[..Math.Min(name.Length, Math.Max(0, (int)(width / 7) - 1))] : "";
            _ = svg.Append("<g class=\"frame\" data-x=\"").Append(frame.X).Append("\" data-w=\"")
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

        _ = svg.Append("<script><![CDATA[const full=").Append(count)
            .Append(
                ";function draw(x,w,d){document.querySelectorAll('.frame').forEach(g=>{const gx=+g.dataset.x,gw=+g.dataset.w,gd=+g.dataset.d;const a=gd<d&&gx<=x&&gx+gw>=x+w,c=gd>=d&&gx>=x&&gx+gw<=x+w;g.style.display=a||c?'':'none';if(!a&&!c)return;const px=a?10:10+(gx-x)/w*1380,pw=a?1380:gw/w*1380,r=g.querySelector('rect'),t=g.querySelector('text');r.setAttribute('x',px);r.setAttribute('width',pw);t.setAttribute('x',px+3);t.textContent=pw<14?'':g.dataset.name.slice(0,Math.max(0,Math.floor(pw/7)-1));});}function zoom(g){draw(+g.dataset.x,+g.dataset.w,+g.dataset.d)}function reset(){draw(0,full,0)}")
            .Append(
                "function search(){const term=prompt('Highlight function name (empty to clear)');if(term===null)return;document.querySelectorAll('.frame').forEach(g=>{const hit=term&&g.dataset.name.toLowerCase().includes(term.toLowerCase()),r=g.querySelector('rect');r.style.stroke=hit?'#6000ee':'';r.style.strokeWidth=hit?'2':'';});}]]></script></svg>\n");
        return svg.ToString();
    }
}