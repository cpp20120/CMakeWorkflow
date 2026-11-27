# P3 Cap’n Proto and document-store performance

## Method

The fresh codec comparisons use BenchmarkDotNet 0.15.8, .NET SDK 10.0.114, .NET 10.0.14 x64 RyuJIT and a Ryzen 7 6800H on CachyOS. Each case ran in one launch with four warmups and nine 150 ms measurement iterations. The 4-, 65-, 256- and 512-node fixtures are the existing workflow matrices. The 256-node case has 3,960 edges. The baseline was captured before the P3 reader and adapter changes. The suite checks equivalence of the lookup and traversal results at setup. The host ran at varying frequencies and some decode estimates have wide intervals, so small changes and ratios should be interpreted cautiously.

## Evidence and changes

- A Linux perf run sampled about 2,000 events over 32.9 billion userspace cycles in a typed dense-traversal and decode probe. Sampling and the user-mode cycle counter worked. The JIT memfd symbols were unresolved in the report, which prevented attribution by method.
- RyuJIT disassembly of the traversal showed stack slots beyond 0x700 bytes, aggregate state moves, and calls through the generic scalar-width dispatch in the dependency loop. With the runtime-only change, the optimized dumps showed the hot scalar operation inlined without the same generic dispatch calls. The generated Tier-1 code remained around 8 KB. NativeAOT disassembly of workflow materialization showed a 0x548-byte local stack reservation with several saved registers. These are indications of copying and register pressure. They do not isolate the cost of a particular struct copy.
- The runtime specializes exact-width UInt16 and UInt32 primitive list reads. The fast path checks the index and uses the validated segment and layout. Other widths and list evolution still use the generic checked path. This also accelerates the existing generated primitive readers without changing emitted source.
- The workflow adapter reuses resolved operation and input views in DTO projection. It pre-sizes materialized collections and reads node IDs by index directly for selection and traversal. Canonical bytes and the official interop corpus still pass.
- The P2 emitter’s foreach reader carries a list reader plus an index. MoveNext tests the count and Current goes through the checked indexer. The primitive change improves absolute foreach time. Its gap to indexing remains. A cursor API or broad reader layout redesign was deferred because the available profiles did not isolate enough benefit to justify the changes to checked traversal and list evolution.
- Binary sessions retain complete verification evidence. Individual field accesses still check the requested layout and text. Verified DTO materialization skips the repeated full wire walk. No per-field cache or pooling is used.
- Windows ETW was unavailable. No power-loss or OS page-cache eviction benchmarks were run.

## Workflow codec

Fresh BenchmarkDotNet means. Use the relative results alongside the independent historical references below.

| Nodes | Operation | Fresh P2 | P3 | P2 → P3 allocation |
| ---: | --- | ---: | ---: | ---: |
| 4 | Indexed lookup | 166.4 ns | 147.7 ns | 0 → 0 B |
| 4 | Traversal | 0.562 µs | 0.526 µs | 0 → 0 B |
| 4 | DTO | 3.98 µs | 3.77 µs | 3,864 → 3,752 B |
| 4 | Mapped lookup | 10.68 µs | 10.70 µs | 536 → 536 B |
| 65 | Indexed lookup | 160.9 ns | 149.7 ns | 0 → 0 B |
| 65 | Traversal | 9.32 µs | 13.64 µs | 0 → 0 B |
| 65 | DTO | 97.04 µs | 51.40 µs | 52,416 → 48,952 B |
| 65 | Mapped lookup | 12.93 µs | 11.17 µs | 536 → 536 B |
| 256 | Indexed lookup | 159.2 ns | 170.0 ns | 0 → 0 B |
| 256 | Dense traversal | 261.40 µs | 217.99 µs | 0 → 0 B |
| 256 | DTO | 500.32 µs | 322.90 µs | 267,824 → 218,976 B |
| 256 | Mapped lookup | 14.06 µs | 14.79 µs | 536 → 536 B |
| 512 | Indexed lookup | 161.9 ns | 157.7 ns | 0 → 0 B |
| 512 | Traversal | 32.14 µs | 31.74 µs | 0 → 0 B |
| 512 | DTO | 813.44 µs | 454.76 µs | 389,008 → 360,448 B |
| 512 | Mapped lookup | 15.85 µs | 14.68 µs | 536 → 536 B |

The isolated DTO allocation reductions are reproducible within the fixture sizes. Fresh P2 decode had a wide interval, particularly at 256 and 512 nodes, and the optimized 65-node traversal was slower in this run. Baseline and optimized disassembly were also collected separately with just the runtime reader change applied. Workflow timing measures the combined runtime and adapter changes.

Historical reference values mix separate processes and, for P0, different validation behavior.

| Operation | P0 | P1 | Historical P2 | P3 fresh mean |
| --- | ---: | ---: | ---: | ---: |
| 512 indexed lookup | 57 ns | 153 ns | 157 ns | 157.7 ns |
| 512 traversal | 8.609 µs | 29.435 µs | 30.017 µs | 31.740 µs |
| 512 DTO | 310.617 µs | 351.753 µs | 487.934 µs | 454.755 µs |
| Dense traversal | — | 221.532 µs | 286.723 µs | 217.987 µs |
| Dense DTO | — | 227.739 µs | 367.993 µs | 322.903 µs |
| 512 mapped lookup | 14.214 µs | 14.591 µs | 14.672 µs | 14.683 µs |

The dense traversal reached the P1 reference. DTO throughput remains above the historical P1 latency. Full materialization still validates wire structure on a standalone decode and resolves and decodes text into strings and allocates domain collections. A verified session moves the full wire check into the initial open. The P1 lookup reference is close to the fresh P3 result. P0 remains a different validation baseline.

### Primitive enumeration

A 512-element UInt16 list. Historical P2 and P3 use the same generated fixture and BDN configuration. The P3 change here comes from the runtime.

| Loop | Historical P2 mean | P3 mean | P3 allocated |
| --- | ---: | ---: | ---: |
| Manual wire indexing | 3.639 µs | 2.624 µs | 0 B |
| Generated indexing | 3.649 µs | 2.213 µs | 0 B |
| Generated foreach | 4.562 µs | 2.894 µs | 0 B |

Foreach remains about 31% slower than generated indexing in the P3 run. The absolute gap decreased from 0.913 to 0.681 µs. The P2 manual timing and P3 manual timing have wide intervals.

### Builder and publication copies

The builder originally started at 256 bytes and resized on demand as it initialized each inline composite list. Encoding now reserves a lower bound derived from generated root, node and context layouts, clamped by the same arena limits. The reserve is separate from list validation and does not change encoded offsets. A Release probe warmed each size with 3,000 encodes and took the median of seven 1,000-encode rounds. It timed only WorkflowEncoding.Encode and used per-thread allocation counters.

| Nodes | Baseline allocated | Reserved allocated | Baseline / reserved time |
| ---: | ---: | ---: | ---: |
| 4 | 5,032 B | 4,824 B | 11.4 / 12.2 µs |
| 65 | 67,559 B | 67,517 B | 21.4 / 34.3 µs |
| 256 | 523,688 B | 418,480 B | 222.5 / 280.6 µs |
| 512 | 297,320 B | 300,520 B | 116.2 / 158.2 µs |

This experiment recovered about 103 KiB per dense encode. It did not demonstrate a latency improvement and increased allocation slightly at 512 nodes. The reserve only covers fixed structural space, so variable-sized data can still cause a resize. Rehearsal also reuses its private bounded JSON snapshot and parsed DTO, removing an input-sized copy and a second JSON parse. Canonicalization still walks the segment into its own arena and returns an owned copy. Framing allocates the exact physical payload, which is then hashed and staged. A destination-buffer API and pooling would require separate ownership and memory measurements.

## End-to-end storage

The disk-backed harness used the same NVMe ext4 filesystem for both payload formats. The medians use one discarded iteration and five measured iterations with distinct contents to avoid deduplication. Concurrency and GC are one observation per size. The cold column comes from a new process and fresh store with first-use costs and normal OS caches. It does not imply eviction of filesystem or shared library pages. The process-wide allocation counter includes runtime and worker activity. The reused field result averages 1,000 scoped callbacks. Column values are milliseconds unless otherwise indicated.

For the equivalent durability comparison, binary publish and legacy JSON import use the same CAS operations, SQLite policy and post-install verification. JSON serialization is shown separately because import starts with already serialized exact bytes. Binary publish includes binary normalization, encoding, canonicalization and offline graph validation. Exact-only buffered opens provide an equivalent verification comparison. Full JSON open parses its DTO. Full binary open also verifies canonical form and validates its graph. Production recipe writing is a separate filesystem policy and lacks the catalog transaction and parent-directory synchronization used here.

| Step | 4 warm | 65 warm | 256 dense warm | 512 warm | 512 fresh process |
| --- | ---: | ---: | ---: | ---: | ---: |
| Initialize catalog and CAS | 160.375 | 67.702 | 69.563 | 72.678 | 138.724 |
| Binary encode + normalization | 0.042 | 0.306 | 1.421 | 0.293 | 14.082 |
| Canonicalize | 0.026 | 0.322 | 0.110 | 0.094 | 4.385 |
| Frame | 0.002 | 0.005 | 0.006 | 0.008 | 0.464 |
| Hash frame and canonical segment | 0.020 | 0.037 | 0.070 | 0.093 | 5.210 |
| Production JSON serialization | 0.077 | 0.291 | 1.775 | 0.659 | 55.395 |
| Production recipe write (different policy) | 8.564 | 8.843 | 10.458 | 9.726 | 12.892 |
| Binary publish | 43.440 | 45.803 | 48.890 | 45.628 | 81.603 |
| Legacy JSON import | 43.372 | 49.588 | 46.087 | 46.921 | 87.806 |
| Duplicate binary publish | 1.708 | 4.290 | 5.396 | 2.952 | 9.136 |
| Payload stage write | 0.066 | 0.121 | 0.163 | 0.127 | 2.820 |
| Payload flush to disk | 8.001 | 8.020 | 8.038 | 8.106 | 7.757 |
| Create-only install and parent sync | 8.166 | 9.296 | 8.309 | 8.299 | 8.547 |
| Installed payload verification and descriptor insert | 0.509 | 0.508 | 0.389 | 0.485 | 7.136 |
| SQLite commit | 16.305 | 16.432 | 16.667 | 16.569 | 16.176 |
| Exact-only buffered binary open | 0.826 | 1.025 | 0.707 | 0.752 | 1.075 |
| Exact-only buffered JSON open | 1.203 | 1.229 | 1.500 | 1.139 | 5.580 |
| Full buffered JSON open | 1.155 | 3.014 | 4.343 | 4.420 | 7.414 |
| Full buffered binary open | 1.021 | 2.962 | 1.915 | 1.828 | 6.343 |
| Full mapped binary open | 0.950 | 2.512 | 1.782 | 1.666 | 6.187 |
| Reused buffered lookup (µs) | 2.058 | 2.522 | 0.267 | 0.254 | 3.027 |
| Reused mapped lookup (µs) | 1.880 | 2.533 | 0.324 | 0.261 | 1.605 |
| DTO from verified session | 0.065 | 0.465 | 0.489 | 0.477 | 2.854 |
| Full borrowed traversal | 0.009 | 0.138 | 0.478 | 0.093 | 0.671 |
| Full materialized traversal (µs) | 0.243 | 3.521 | 11.377 | 1.208 | 11.508 |
| Four concurrent full mapped opens and repeated lookups | 158.065 | 155.278 | 155.364 | 101.867 | 176.157 |
| Four concurrent duplicate publications | 154.105 | 153.760 | 155.883 | 103.607 | 108.752 |
| Collect unrooted rows and blobs | 134.400 | 132.463 | 122.032 | 128.774 | 47.011 |

Sizes and allocation per operation in bytes, from the warm disk-backed run. Phase allocation was not sampled separately. The catalog footprint is measured after the close of the last SQLite connection and before collection.

| Metric | 4 | 65 | 256 dense | 512 |
| --- | ---: | ---: | ---: | ---: |
| Legacy JSON file size | 2,447 | 33,923 | 233,482 | 257,920 |
| Official framed blob size | 896 | 11,144 | 52,224 | 72,640 |
| Catalog footprint | 36,864 | 36,864 | 36,864 | 36,864 |
| Binary encode allocation | 4,888 | 67,608 | 418,968 | 301,456 |
| Binary publication allocation | 129,800 | 326,200 | 1,531,296 | 1,224,152 |
| JSON import allocation | 132,048 | 384,040 | 1,566,384 | 2,220,992 |
| Full buffered binary open allocation | 40,760 | 152,128 | 788,024 | 773,264 |
| Full mapped binary open allocation | 40,000 | 141,120 | 735,936 | 700,760 |
| Reused mapped lookup allocation | 0 | 0 | 0 | 0 |
| Materialization from verified session allocation | 3,864 | 49,064 | 219,936 | 362,368 |

The warm process peaked at about 90 MiB RSS. The new 512-node process peaked at about 72 MiB. On this filesystem the file flush, install synchronization and SQLite commit dominate new publication. Duplicate publication saves file staging and synchronization, but still encodes, verifies and takes the catalog lock. The shared CAS lease uses a 50 ms retry interval on contention. The concurrent open and duplicate-publish measurements are consistent with paying several retry intervals. The benchmark includes normal initialization but does not separately isolate WAL checkpoint cost or independently benchmark SQLite contention. Collection removed 12 unretained payloads per warm size and two in the fresh process. It currently commits pruning per physical blob.

Buffered is the default. Mapped reads reduce managed copying by about one frame per open while still paying integrity and graph validation. The difference in first-open latency varied with size and load. For repeated queries, reuse the verified session after the first open. Once open, selective borrowed reads allocate zero bytes in these runs. For repeated full traversals, the measured DTO traversal is cheaper per pass after its one-time materialization cost. No automatic mmap cutoff is inferred from this host.

## Reproduction and NativeAOT

Run from the repository root with .NET 10. To obtain the P2 baseline, run the same BenchmarkDotNet selections on the P2 source state before applying P3.

    dotnet run -c Release --project CMakeWorkflow.Binary.Capnp.Benchmarks -- --buildTimeout 300 --filter '*BinaryLookup' '*BinaryTraverse' '*BinaryDecode' '*BinaryMappedLookup'
    dotnet run -c Release --project CMakeWorkflow.Binary.Capnp.Benchmarks -- --buildTimeout 300 --filter '*BinaryEncode' '*BinaryCanonicalize'
    dotnet run -c Release --project Capnp.Managed.CodeGen.Benchmarks -- --buildTimeout 300 --filter '*Enumeration'
    dotnet run -c Release --project CMakeWorkflow.Storage.Documents.Benchmarks
    dotnet run -c Release --project CMakeWorkflow.Storage.Documents.Benchmarks -- --cold 512
    dotnet run -c Release -p:TreatWarningsAsErrors=true --project CMakeWorkflow.Storage.Documents.Tests -- --oracle /path/to/cmwf-capnp-oracle
    dotnet publish CMakeWorkflow.Storage.Documents.AotSmoke -c Release -r linux-x64 -warnaserror -o out/capnp-documents-aot
    ./out/capnp-documents-aot/CMakeWorkflow.Storage.Documents.AotSmoke

Set TMPDIR to an empty directory on the filesystem you want to measure. The test oracle is built from CMakeWorkflow.Binary.Capnp/Interop against the official Cap’n Proto compiler and library. The native executable and SQLite library were about 4.2 MiB and 1.5 MiB on Linux x64. The executable links glibc, libm and the dynamic loader. Microsoft.Data.Sqlite loads the included libe_sqlite3.so provider, which links glibc and the loader. The Linux program ran through database initialization, publication, identity verification, both owners, generated access, DTO and JSON projection, migration, reopen, retention and collection with AOT and trim warnings treated as errors. Windows x64 is in the experimental CI matrix and requires the bundled native SQLite provider and the Windows NativeAOT toolchain. Windows was unavailable for the local run.
