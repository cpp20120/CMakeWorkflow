using System.Numerics;

namespace CMakeWorkflow.Profiling.Historical;

/// <summary>PCG setseq 128-bit LCG with XSL-RR 64-bit output and fixed stream 54.</summary>
/// <remarks>Reference transition/seeding from imneme/pcg-c; see docs/prng-notices.md. Serial mutable state.</remarks>
internal sealed class HistoricalPcg64
{
    private static readonly UInt128 Multiplier = ((UInt128)2549297995355413924UL << 64) | 4865540595714422341UL;
    private UInt128 state;

    internal HistoricalPcg64(uint seed)
    {
        Step();
        state += seed;
        Step();
    }

    private void Step() => state = unchecked((state * Multiplier) + 109);

    internal ulong Next()
    {
        Step();
        return BitOperations.RotateRight((ulong)(state >> 64) ^ (ulong)state, (int)(state >> 122));
    }
}

/// <summary>Blackman/Vigna xoroshiro128++ 1.0 with two SplitMix64 seed-expansion words.</summary>
/// <remarks>Public-domain reference transition; see docs/prng-notices.md. Instances are not synchronized.</remarks>
internal sealed class HistoricalXoroshiro128
{
    private ulong s0;
    private ulong s1;

    internal HistoricalXoroshiro128(uint seed)
    {
        NRandom.Algorithms.SplitMix64 expansion = new(seed);
        s0 = expansion.Next();
        s1 = expansion.Next();
    }

    internal ulong Next()
    {
        ulong result = unchecked(BitOperations.RotateLeft(s0 + s1, 17) + s0);
        s1 ^= s0;
        s0 = BitOperations.RotateLeft(s0, 49) ^ s1 ^ (s1 << 21);
        s1 = BitOperations.RotateLeft(s1, 28);
        return result;
    }
}

/// <summary>Random123 Threefry4x64-20 with key (seed,0,0,0) and counters starting at zero.</summary>
/// <remarks>Managed reference port; see docs/prng-notices.md. Emits each block's four words in lane order.</remarks>
internal sealed class HistoricalThreefry(uint seed)
{
    private static readonly int[] Rotations = [14, 16, 52, 57, 23, 40, 5, 37, 25, 33, 46, 12, 58, 22, 32, 32];
    private readonly ulong[] block = new ulong[4];
    private ulong counter;
    private int index = 4;

    internal ulong Next()
    {
        if (index == 4)
        {
            Encrypt(counter++, seed, block);
            index = 0;
        }
        return block[index++];
    }

    /// <summary>Reference block function, also exposed internally for upstream known-answer vectors.</summary>
    internal static void Encrypt(ulong counter, ulong key, Span<ulong> result)
    {
        Span<ulong> keys = [key, 0, 0, 0, 0x1BD11BDAA9FC1A22UL ^ key];
        ulong a = unchecked(counter + key), b = 0, c = 0, d = 0;
        for (int round = 0; round < 20; round++)
        {
            a = unchecked(a + b);
            b = BitOperations.RotateLeft(b, Rotations[round % 8 * 2]) ^ a;
            c = unchecked(c + d);
            d = BitOperations.RotateLeft(d, Rotations[(round % 8 * 2) + 1]) ^ c;
            (b, d) = (d, b);
            if ((round + 1) % 4 == 0)
            {
                int injection = (round + 1) / 4;
                a = unchecked(a + keys[injection % 5]);
                b = unchecked(b + keys[(injection + 1) % 5]);
                c = unchecked(c + keys[(injection + 2) % 5]);
                d = unchecked(d + keys[(injection + 3) % 5] + (ulong)injection);
            }
        }
        result[0] = a; result[1] = b; result[2] = c; result[3] = d;
    }
}
