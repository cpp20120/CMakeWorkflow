using System.Runtime.InteropServices;
using NRandom;

namespace CMakeWorkflow.Profiling.Historical;

/// <summary>One serial, explicitly seeded schedule stream; never used by execution or recovery.</summary>
/// <remarks>
/// Versions include the raw-output and seed adapter, not just the algorithm family. Saved schedules remain
/// authoritative across runtime/library changes. Rejection sampling avoids modulo bias in bounded indices.
/// </remarks>
internal sealed class HistoricalRandom(string version, Func<uint> next, Func<int, int>? bounded = null,
    bool enabled = true)
{
    internal string Version { get; } = version;
    internal uint NextUInt32() => next();

    internal static readonly string[] Algorithms =
    ["none", "dotnet", "mt19937", "pcg32", "pcg64", "xoshiro256ss", "xoroshiro128pp",
        "splitmix64", "sfc64", "philox", "threefry", "python-legacy"];

    /// <summary>Creates a fresh stream without reflection, ambient entropy or shared random state.</summary>
    internal static HistoricalRandom Create(string algorithm, uint seed)
    {
        if (algorithm == "none")
        {
            return new("none/v1", () => throw new InvalidOperationException("No PRNG selected."), enabled: false);
        }
        if (algorithm == "dotnet")
        {
            if (seed > int.MaxValue)
            {
                throw new ArgumentException("dotnet seeds must be between 0 and 2147483647.");
            }
            Random random = new((int)seed);
            return new("System.Random/" + RuntimeInformation.FrameworkDescription + "/NextInt64-u32/v1",
                () => DotNetWord(random));
        }
        if (algorithm == "python-legacy")
        {
            LegacyMt19937Shuffle random = new(seed);
            return new("cmwf-python-mt19937-getrandbits/v1", random.Next32, random.Below);
        }
        if (algorithm == "pcg64")
        {
            HistoricalPcg64 random = new(seed);
            return new("pcg-setseq-128-xsl-rr-64/stream54-high32/v1", () => (uint)(random.Next() >> 32));
        }
        if (algorithm == "xoroshiro128pp")
        {
            HistoricalXoroshiro128 random = new(seed);
            return new("xoroshiro128++-1.0/splitmix64-high32/v1", () => (uint)(random.Next() >> 32));
        }
        if (algorithm == "threefry")
        {
            HistoricalThreefry random = new(seed);
            return new("Random123-threefry4x64-20/key-seed-counter0-high32/v1", () => (uint)(random.Next() >> 32));
        }
        if (algorithm == "xoshiro256ss")
        {
            Redzen.Random.Xoshiro256StarStarRandom random = new(seed);
            return new("Redzen/16.0.0/xoshiro256ss/splitmix64-high32/v1", () => (uint)(random.NextULong() >> 32));
        }
        if (algorithm == "philox")
        {
            // NRandom's static primitive increments its counter and mutates its key. Supply fresh
            // copies per block: feeding its encrypted counter back would not be a Philox counter stream.
            ulong counter = 0;
            uint keySeed = seed;
            uint key1 = NRandom.Algorithms.SplitMix32.Next(ref keySeed);
            uint[] block = new uint[4];
            int index = 4;
            uint Next()
            {
                if (index == 4)
                {
                    Span<uint> words = stackalloc uint[4];
                    words.Clear();
                    words[0] = (uint)counter;
                    words[1] = (uint)(counter >> 32);
                    Span<uint> key = [seed, key1];
                    NRandom.Algorithms.Philox4x32.Next(words, key, 10);
                    words.CopyTo(block);
                    counter++;
                    index = 0;
                }
                return block[index++];
            }
            return new("NRandom/2.0.2/philox4x32-10/counter1-splitmix32-key/v1", Next);
        }
        IRandom generator = algorithm switch
        {
            "mt19937" => new MersenneTwisterRandom(),
            "pcg32" => new Pcg32Random(),
            "splitmix64" => new SplitMix64Random(),
            "sfc64" => new Sfc64Random(),
            _ => throw new ArgumentException("Unknown shuffle algorithm: " + algorithm)
        };
        generator.InitState(seed);
        return new("NRandom/2.0.2/" + algorithm + "/InitState-u32-NextUInt/v1", generator.NextUInt);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394",
        Justification = "Explicit System.Random benchmark ordering option; no security or unpredictability requirement.")]
    private static uint DotNetWord(Random random) => (uint)random.NextInt64(1L << 32);

    /// <summary>Uses descending Fisher-Yates; none leaves the caller's list untouched.</summary>
    internal void Shuffle<T>(IList<T> values)
    {
        if (!enabled) { return; }
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = bounded is null ? SelectIndex(next, i + 1) : bounded(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>Rejects the incomplete residue prefix so each accepted index has equal multiplicity.</summary>
    /// <param name="draw">Full-width unsigned draws; repeated rejection advances this stream.</param>
    /// <param name="exclusive">Positive exclusive index bound.</param>
    internal static int SelectIndex(Func<uint> draw, int exclusive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusive, 1);
        uint bound = (uint)exclusive;
        uint threshold = unchecked(0U - bound) % bound;
        uint word;
        do { word = draw(); } while (word < threshold);
        return (int)(word % bound);
    }
}
