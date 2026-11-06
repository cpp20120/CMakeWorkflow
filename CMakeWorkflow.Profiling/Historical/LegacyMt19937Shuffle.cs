// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Historical;
#pragma warning restore IDE0130

// LegacyMt19937Shuffle random.Random integer seed and _randbelow/getrandbits order used by
// the archived DagFlow experiment. This preserves its paired shuffle schedule.
/// <summary>Mutable deterministic shuffle state for the archived experiment schedule.</summary>
/// <remarks>
/// Uses the Python-style integer-seed and rejection-sampling draw order used by the historical
/// experiment. Reproducibility depends on both the seed and the preceding sequence of shuffle calls.
/// Instances and caller-owned lists are not synchronized; use one serial schedule owner.
/// </remarks>
internal sealed class LegacyMt19937Shuffle
{
    private readonly uint[] state = new uint[624];
    private int index = 624;

    internal LegacyMt19937Shuffle(uint seed)
    {
        state[0] = 19650218;
        for (int i = 1; i < state.Length; i++)
        {
            state[i] = unchecked((1812433253u * (state[i - 1] ^ (state[i - 1] >> 30))) + (uint)i);
        }

        int j = 0;
        for (int k = Math.Max(624, 1); k > 0; k--)
        {
            int i = 1 + ((Math.Max(624, 1) - k) % 623);
            state[i] = unchecked((state[i] ^ ((state[i - 1] ^ (state[i - 1] >> 30))
                                              * 1664525u)) + seed + (uint)j);
            j = 0;
            if (i == 623)
            {
                state[0] = state[623];
            }
        }

        for (int k = 623; k > 0; k--)
        {
            int i = 1 + ((624 - k) % 623);
            state[i] = unchecked((state[i] ^ ((state[i - 1] ^ (state[i - 1] >> 30))
                                              * 1566083941u)) - (uint)i);
            if (i == 623)
            {
                state[0] = state[623];
            }
        }

        state[0] = 0x80000000;
    }

    internal uint Next32()
    {
        if (index >= 624)
        {
            for (int i = 0; i < 624; i++)
            {
                uint y = (state[i] & 0x80000000) | (state[(i + 1) % 624] & 0x7fffffff);
                state[i] = state[(i + 397) % 624] ^ (y >> 1)
                                                  ^ ((y & 1) == 0 ? 0u : 0x9908b0dfu);
            }

            index = 0;
        }

        uint value = state[index++];
        value ^= value >> 11;
        value ^= (value << 7) & 0x9d2c5680;
        value ^= (value << 15) & 0xefc60000;
        value ^= value >> 18;
        return value;
    }

    internal int Below(int exclusive)
    {
        int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)exclusive);
        uint value;
        do
        {
            value = Next32() >> (32 - bits);
        } while (value >= exclusive);

        return (int)value;
    }

    /// <summary>Reorders the supplied mutable list in place while advancing this experiment random stream.</summary>
    /// <remarks>
    /// The same seed and call history reproduce ordering. This changes input order only; it does not persist
    /// which measurements actually executed, so recovery still uses the verified command journal.
    /// </remarks>
    internal void Shuffle<T>(IList<T> values)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = Below(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}