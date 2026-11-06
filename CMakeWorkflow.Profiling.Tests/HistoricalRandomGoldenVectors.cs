using System.Diagnostics;
using CMakeWorkflow.Profiling.Historical;

namespace CMakeWorkflow.Profiling.Tests;

internal static class HistoricalRandomGoldenVectors
{
    // Seed-profile vectors. PCG64/Threefry/Philox were cross-checked against the pinned upstream
    // C/C++ implementations listed in docs/prng-notices.md; Python against Random(42).getrandbits(32).
    // NRandom's remaining InitState profiles and .NET 10's seeded adapter are frozen here as compatibility fixtures.
    internal static void Run()
    {
        Dictionary<string, uint[]> vectors = new(StringComparer.Ordinal)
        {
            ["dotnet"] = [539095617, 1127826097, 745826356, 1105187776, 1636250911, 151693383, 1707125052, 3028603249],
            ["mt19937"] = [1608637542, 3421126067, 4083286876, 787846414, 3143890026, 3348747335, 2571218620, 2563451924],
            ["pcg32"] = [559531164, 739919358, 3338269888, 2656664263, 940337511, 3242248412, 1766051887, 1656819616],
            ["pcg64"] = [2259802653, 319072838, 2741440158, 4178120274, 3361447896, 1616978424, 2095171583, 3418664048],
            ["xoshiro256ss"] = [360188718, 1627707782, 2920764210, 3971525959, 4259765375, 3306005809, 3089192069, 3650758467],
            ["xoroshiro128pp"] = [3901421258, 1419969458, 4084236588, 878197190, 1472442894, 394945992, 2104591528, 427213598],
            ["splitmix64"] = [3184996902, 686809907, 1196582743, 1478287871, 163338330, 3729011194, 938043164, 3438687726],
            ["sfc64"] = [2233722891, 1861037657, 1772215557, 2585286101, 704342977, 3063920704, 610506753, 930729605],
            ["philox"] = [2572967592, 743078585, 1328031971, 2331549415, 3677593566, 655665339, 883324723, 3730511197],
            ["threefry"] = [1152842893, 1823806907, 887630368, 1326568631, 4239543330, 1454407815, 901152194, 597337241],
            ["python-legacy"] = [2746317213, 478163327, 107420369, 3184935163, 1181241943, 1051802512, 958682846, 599310825]
        };
        foreach ((string algorithm, uint[] expected) in vectors)
        {
            HistoricalRandom random = HistoricalRandom.Create(algorithm, 42);
            uint[] actual = [.. Enumerable.Range(0, expected.Length).Select(_ => random.NextUInt32())];
            if (!actual.SequenceEqual(expected)) { throw new UnreachableException("PRNG golden vector changed: " + algorithm); }
            HistoricalRandom repeat = HistoricalRandom.Create(algorithm, 42);
            if (!Enumerable.Range(0, expected.Length).Select(_ => repeat.NextUInt32()).SequenceEqual(expected))
            {
                throw new UnreachableException("PRNG seed is not deterministic: " + algorithm);
            }
        }
        ulong[] zero = new ulong[4];
        HistoricalThreefry.Encrypt(0, 0, zero);
        if (!zero.SequenceEqual([0x09218ebde6c85537UL, 0x55941f5266d86105UL, 0x4bd25e16282434dcUL, 0xee29ec846bd2e40bUL]))
        {
            throw new UnreachableException("Threefry4x64-20 upstream zero-counter/key KAT failed.");
        }
        List<int> unchanged = [0, 1, 2, 3, 4];
        HistoricalRandom.Create("none", 42).Shuffle(unchanged);
        if (!unchanged.SequenceEqual([0, 1, 2, 3, 4])) { throw new UnreachableException("none changed order."); }
        // For bound 10 the six-word incomplete residue prefix must be rejected. A statistical
        // frequency test alone would almost never detect the bias from naive uint % bound.
        uint draw = 0;
        int[] counts = new int[10];
        for (int i = 0; i < 300; i++) { counts[HistoricalRandom.SelectIndex(() => draw++, 10)]++; }
        if (draw != 306 || counts.Any(count => count != 30))
        {
            throw new UnreachableException("Bounded selection did not reject the biased residue prefix.");
        }
        Console.WriteLine("PRNG vectors, deterministic seeds, none and rejection-sampling checks passed.");
    }
}