# Historical benchmark random streams

`CMakeWorkflow.Profiling` pins **NRandom 2.0.2** (MIT). The restored NuGet package
identifies source commit `45200be63e21f8868cfab7a0dec9465ecfc7537c` in
[nuskey8/NRandom](https://github.com/nuskey8/NRandom). Its net8.0 asset has no runtime
package dependencies and is used from .NET 10. No reflection-based registration,
assembly scanning or runtime code generation is used by the scheduling adapters.
The xoshiro256** adapter uses **Redzen 16.0.0** (MIT), also a net8.0 asset with
no runtime package dependencies. Its reference transition is checked against the
Blackman/Vigna vector; NRandom 2.0.2's xoshiro256 implementation is deliberately
not used because its shifts differ from the published algorithm.

NativeAOT compatibility is exercised by the profiling smoke executable, including
all raw-output golden vectors and schedule persistence/recovery paths.

| CLI ID | Actual implementation and seed/output profile |
| --- | --- |
| `none` | No generator; skips random permutations |
| `dotnet` | `System.Random((int)seed).NextInt64(2^32)`; runtime version recorded; seeds 0..Int32.MaxValue |
| `mt19937` | NRandom `MersenneTwisterRandom.InitState(uint).NextUInt()` (32-bit MT19937) |
| `pcg32` | NRandom `Pcg32Random.InitState(uint).NextUInt()` (PCG-XSH-RR, package-defined stream/seeding) |
| `pcg64` | PCG setseq XSL-RR 128/64, sequence 54, upstream seeding with initstate=seed; high 32 output bits |
| `xoshiro256ss` | Redzen xoshiro256**, SplitMix64 state expansion, high 32 output bits |
| `xoroshiro128pp` | xoroshiro128++ 1.0, two SplitMix64 state words, high 32 output bits |
| `splitmix64` | NRandom SplitMix64 with state=seed, high 32 output bits |
| `sfc64` | NRandom SFC64, a=b=c=seed, counter=1, 12 warmup draws, high 32 output bits |
| `philox` | Philox4x32-10: key=(seed,SplitMix32(seed)), counter starts at 1; lane order 0..3 |
| `threefry` | Threefry4x64-20: key=(seed,0,0,0), counter starts at 0; lane order 0..3, high 32 output bits |
| `python-legacy` | Existing Python MT19937 integer-seed/getrandbits/rejection profile, retained for archival ordering |

The Philox adapter calls NRandom's round primitive with **fresh copies of the
counter and key** for each block. It does not chain the encrypted counter/key
back into the next block. PCG64, xoroshiro128++ and Threefry use small managed
reference ports; they are not aliases for another generator. The selected NuGet
API does not supply those three variants. This avoids a second broad dependency
or a commercial RNG dependency merely to expose these transitions.

Reference sources and pinned validation revisions:

- [PCG C](https://github.com/imneme/pcg-c), revision
  `83252d9c23df9c82ecb42210afed61a7b42402d7`, `pcg_setseq_128_srandom_r` and
  `pcg_setseq_128_xsl_rr_64_random_r`; MIT option, notice in [pcg-MIT.txt](licenses/pcg-MIT.txt).
- [Blackman/Vigna xoroshiro128++](https://prng.di.unimi.it/xoroshiro128plusplus.c),
  reference transition 1.0, public domain with permission/disclaimer retained in
  [xoroshiro-public-domain.txt](licenses/xoroshiro-public-domain.txt).
- [D. E. Shaw Research Random123](https://github.com/DEShawResearch/random123),
  revision `9545ff6413f258be2f04c1d319d99aaef7521150`, Threefry4x64-20 and
  Philox4x32-10; BSD notice in [Random123-BSD.txt](licenses/Random123-BSD.txt).
- [NRandom MIT notice](licenses/NRandom-MIT.txt).
- [Redzen](https://github.com/colgreen/Redzen), NuGet source revision
  `42861e2adf6d6063ff48ec8a3921d689860b737a`, [MIT notice](licenses/Redzen-MIT.txt).

The test vectors freeze eight uint32 draws at seed 42 for every PRNG/adapter,
plus Random123's full four-word zero-counter/key Threefry known-answer test.
PCG64/Threefry/Philox adapters were compared against compiled upstream C/C++
implementations, MT19937 against `std::mt19937`, and archival output/order against
Python `random.Random`. The bounded-index test explicitly exercises rejection of
the incomplete residue prefix; a frequency test alone would miss this tiny bias.

All new schedule streams use UInt32 CLI seeds, with the narrower documented
`dotnet` range. PRNG ID, adapter version, seed, strategy, scope, workload/configuration
identity and every ordered measurement are saved. A different raw generator,
seed expansion or index sampler requires a new adapter version. `dotnet` is
identified by runtime and does not promise identical newly generated schedules
across arbitrary .NET releases. Recovery uses the saved order, so it does not
require the original PRNG runtime to remain installed.
