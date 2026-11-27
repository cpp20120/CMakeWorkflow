# ADR-0004 — Cap’n Proto for immutable workflow manifests

**2026-11-27 · Experimental decision**

This ADR records the P0 baseline. The runtime extraction and multi-segment support are documented in [ADR-0005](0005-managed-capnp-wire-runtime.md).

Keep a narrow managed Cap’n Proto backend as a feasibility prototype. It is a viable option for reading selected fields of an immutable manifest without loading a .NET object graph. Continue using the existing JSON persistence and identity contracts. A production decision requires workload evidence for partial reads and a design for immutable file publication, schema ownership, and compatibility. A general .NET Cap’n Proto runtime is outside this proposal.

## Repository audit

| Area | Existing contract and implication |
| --- | --- |
| Manifest JSON | Version 1 and 2 use source-generated camel-case JSON. Recipe persistence writes UTF-8 with a final LF. The scripting snapshot uses the SHA-256 of its exact bytes and publishes by content-derived filename. Existing readers, byte formatting and content identities are compatibility constraints. [WorkflowDefinitionJson](../../CMakeWorkflow.Contracts/Workflows/WorkflowDefinitionJson.cs) · [WorkflowManifestBytes](../../CMakeWorkflow.CMake/Serialization/WorkflowManifestBytes.cs) · [Scripting snapshot](../../CMakeWorkflow.Scripting/WorkflowManifest.cs) |
| Workflow plans | The planner validates the full DAG, including unselected branches, and combines explicit edges, run/profile producers, and artifact producers. It uses declaration order among ready nodes, resolves paths relative to the original manifest, adds observed host state, and carries the original byte hash into the plan. A portable manifest is a smaller persistence target than a host-bound execution plan. [WorkflowPlanner](../../CMakeWorkflow.CMake/Planning/WorkflowPlanner.cs) · [WorkflowDag](../../CMakeWorkflow.CMake/Planning/WorkflowDag.cs) · [WorkflowPlan](../../CMakeWorkflow.CMake/Planning/WorkflowPlan.cs) |
| Host integrity and recovery | Manifest publication uses create-only publication with path anchoring, confirms length and the exact-byte digest, and treats accepted bytes as durable evidence. Process execution and recovery rely on separate journals and checkpoints. [ManifestPublication](../../CMakeWorkflow.Host/Workflows/ManifestPublication.cs) · [ProcessEventJournal](../../CMakeWorkflow.Host/Execution/ProcessEventJournal.cs) · [ProcessWorkflowRecovery](../../CMakeWorkflow.Host/Execution/ProcessWorkflowRecovery.cs) |
| CAS and Action Cache | The artifact CAS verifies exact blob length and SHA-256 under a store lease. Action keys hash a versioned length-prefixed sequence containing executable evidence, host, configuration, arguments, inputs and effective environment. An action entry is a bounded JSON reference to the output blob. A future canonical-manifest digest over an unframed segment would require a distinct identity contract if the stored object is framed. [ArtifactStoreFiles](../../CMakeWorkflow.Core/Persistence/ArtifactStoreFiles.cs) · [WorkflowActionCache](../../CMakeWorkflow.CMake/Execution/WorkflowActionCache.cs) · [CasActions](../../CMakeWorkflow.Core/Persistence/CasActions.cs) |
| Environment and dependency plans | Managed environment locks publish and verify pinned serialized bytes using lowercase SHA-256. The dependency session recomputes the source-bound vcpkg plan and checks that the workflow, source snapshot, plan approval and toolchain environment agree before installation. Tooling plan approvals also hash their source-generated JSON. [ManagedEnvironmentLocks](../../CMakeWorkflow.Tooling/Tools/ManagedEnvironmentLocks.cs) · [VcpkgProjects](../../CMakeWorkflow.Tooling/Tools/VcpkgProjects.cs) · [ExperimentDependencySession](../../CMakeWorkflow.Host/Sessions/ExperimentDependencySession.cs) · [ToolingPlanApproval](../../CMakeWorkflow.Tooling/Tools/ToolingPlanApproval.cs) |
| SQLite | History stores relational runs, nodes, measurements and artifacts with schema migrations. The pipeline ledger uses foreign keys, WAL and full synchronous durability for pipeline and child-launch state. These transactional responsibilities have different requirements from immutable blob encoding. [HistoryStore](../../CMakeWorkflow.History/Storage/HistoryStore.cs) · [PipelineLedger](../../CMakeWorkflow.Pipelines.Persistence/PipelineLedger.cs) |
| NativeAOT | The CLI targets .NET 10 and NativeAOT with invariant globalization. The Windows ETW decoder is published separately because it uses reflection. Existing smoke projects establish the pattern for an isolated AOT consumer. [CLI project](../../CMakeWorkflow.csproj) · [Scripting AOT smoke](../../CMakeWorkflow.Scripting.AotSmoke/CMakeWorkflow.Scripting.AotSmoke.csproj) |

The prototype uses the existing schema-v2 `WorkflowDocument`. The interoperability fixture exercises configure, build, run and CTest nodes, including explicit and implicit edges, input/output evidence, configuration, environment, timeout, resource and CPU constraints. Benchmarks build actual CTest and build matrices using the production recipe builders.

## Alternatives

| Approach | Findings | Decision |
| --- | --- | --- |
| Keep source-generated JSON | Existing contract and broad schema coverage. A consumer can index a loaded DTO cheaply. Reading one node from persisted bytes still pays for parsing somewhere, and existing identities are over exact serialized bytes. | Keep production persistence. |
| `c80k/capnproto-dotnetcore` | MIT. NuGet latest stable `1.3.118` was released 2020-04-27 and inspected HEAD `8563c80` is from 2022-02-13. It supports generated readers, direct state views over caller-provided word memory, and reports C++ compatibility tests. A Linux x64 NativeAOT probe against the official fixture published without trimming warnings and read the version, node count, and node ID. It allocated 256 B per list-index and text-read operation and 1,264 B per stream-frame read on the same fixture. Stream framing allocates segment arrays. The probe reused the parsed frame and list for text reads. The runtime has reflection-based dynamic generic and RPC paths outside the probe. The project lists canonicalization as unsupported and exposes no file-mapping owner in the reviewed framing API. A custom mapped memory owner and canonicalizer would still be needed. [Runtime package](https://www.nuget.org/packages/Capnp.Net.Runtime/1.3.118) · [Features](https://github.com/c80k/capnproto-dotnetcore/tree/8563c80629c79440f797685e42ba403c4ed5d579#features) · [Framing source](https://github.com/c80k/capnproto-dotnetcore/blob/8563c80629c79440f797685e42ba403c4ed5d579/Capnp.Net.Runtime/Framing.cs) · [Runtime reflection source](https://github.com/c80k/capnproto-dotnetcore/blob/8563c80629c79440f797685e42ba403c4ed5d579/Capnp.Net.Runtime/CapnpSerializable.cs) | Pass on adoption for this contract. |
| Earlier C# implementations | The MIT-licensed 8th Wall fork of Marc Gravell's implementation was last updated at inspected HEAD `6c80919` in 2016. It has value-type typed views and a memory-mapped segment factory. Its type accessor uses runtime generic reflection. The Thomas Brix Larsen implementation targets the older Mono/xbuild workflow and was last updated at inspected HEAD `9ea9245` in 2017. No top-level license was found in that checkout. Canonical encoding, compatibility against the current oracle, allocations and NativeAOT were unverified for both. [8th Wall fork](https://github.com/8thwall/capnproto-net) · [Mapping source](https://github.com/8thwall/capnproto-net/blob/6c80919/CapnProto.net/MemoryMappedFileSegmentFactory.cs) · [Thomas Brix Larsen implementation](https://github.com/ThomasBrixLarsen/capnproto-dotnet) | Pass on adoption. |
| Bind the official C++ runtime | Offers the reference wire implementation and canonicalizer. A C ABI, native packaging for each RID, and mapping ownership across the interop boundary would still be needed. | Use as an independent test oracle. |
| Narrow managed single-segment codec | Uses typed spans for reads, a growable word arena for writes, and an explicit single-segment canonicalization pass. It publishes under NativeAOT without a Cap’n Proto runtime dependency. Hand-maintained layout and parser coverage carry ongoing costs. | Retain for experiments with partial reads. |

## Identity and wire format

1. **Domain normalization** orders context and environment maps by ordinal key. Explicit prerequisites are deduplicated and sorted by declaration index. Node declaration, argument, resource, and artifact sequences are preserved. Distinct literal values, including null and empty text for optional fields, are preserved. The authoring label is absent from the existing wire document, so the prototype derives manifest identity from content.
2. **Wire canonicalization** walks reachable objects in pointer preorder, removes trailing zero data and pointer words from structs and uniformly from inline composite lists, writes the special empty struct offset, and zeroes primitive-list padding. The schema-independent pass preserves reachable unknown fields. Materializing through the old DTO drops unknown fields. Keep the original segment or canonicalize it directly when forwarding a newer message.
3. **Transport framing** prepends the eight-byte segment table for one unpacked segment. The framed bytes can be passed to the official CLI or stored in an immutable file.
4. **Content hash** is lowercase SHA-256 of the unframed canonical segment, following the official canonical hashing rule. For the rich fixture the digest is `a855f400b61bcf99bf4e8a51926ef026702b2eddd66a49c04acf585c6bb71feb`. [Official encoding specification](https://capnproto.org/encoding.html#canonicalization)

Each node has a `List(UInt32)` adjacency row. Run and input producer references are independently accessible. All three refer to the declaration-indexed node list. Flattening explicit edges into CSR would replace per-node near pointers and per-list padding with one offsets array and one edge array. That can help dense matrices or sequential edge scans, but it adds an offset per node and another index validation step for random access. The tested matrices are sparse, including a 512-node matrix with no edges. Keep the adjacency rows until denser representative graphs justify testing CSR.

## Access, boundaries and checks

`WorkflowView` and `NodeView` borrow the caller’s immutable span. Text access returns UTF-8 slices and graph access resolves indices from encoded memory. Field access validates the pointers, bounds and shape it follows. Call `CapnpWire.Validate` to check all reachable wire pointers under a cumulative budget. The typed decoder calls it before materializing the supported DTO fields. DAG, path, and host validation remain the planner's responsibility.

`MappedWorkflow.Read` holds the file, map, view, and acquired pointer for a synchronous callback. The ref-struct view and borrowed spans cannot escape through the generic callback result. Use it for a trusted immutable file. On Unix an external writer that truncates the file during the mapping can cause an OS fault regardless of parser bounds checks. A production store would need an immutability guarantee for mappings and a bounded buffered entry point for mutable input.

The wire subset supports one unpacked segment with near pointers, primitive lists, pointer lists and inline composite lists. Far pointers, multisegment framing, capability pointers, RPC, packed encoding, legacy primitive-to-struct list upgrades, a general schema runtime and code generation are outside its scope. Wire validation limits the segment to 16 MiB, traversal to 4,194,304 work units, and pointer nesting to 32 non-null levels. Typed collections and text including its terminator are limited to 16,384 elements or bytes. Malformed stable input raises `InvalidDataException`. The schema projection rejects unsupported operation kinds and populated toolchain/Project SDK bindings, profile options, benchmark metrics/trials, replay and invoke fields. It handles additive fields at the same domain schema version. Unknown operation discriminants or a new domain schema version require an updated adapter.

The .NET test executable passed round trips, fixed canonical byte and SHA-256 vectors, normalization variants, backward near pointers, all truncations of the rich segment, malformed text/lists and indices, pointer bounds, cycle/nesting checks, amplification limits, mapped access, and 10,000 seeded mutation or random-input cases. The live official 1.2.0 checks passed in both directions using `capnp encode/decode`, byte-for-byte comparison against C++ `capnp::canonicalize`, and C++ `isCanonical()`. The future schema adds root data and pointer fields plus an extra context pointer. Default fields preserve the hash, populated fields survive wire canonicalization, and the older decoder reads its original fields. [Schema](../../CMakeWorkflow.Binary.Capnp/Schema/workflow.capnp) · [Tests](../../CMakeWorkflow.Binary.Capnp.Tests/Program.cs) · [C++ oracle](../../CMakeWorkflow.Binary.Capnp/Interop/oracle.cpp)

## Measurements

Ran on CachyOS Linux x64, AMD Ryzen 7 6800H, .NET SDK 10.0.114 and runtime 10.0.14. The 4- and 65-node cases use sequential CTest groups with 3 and 64 explicit edges. The 512-node case uses 16 build variants and 32 targets. Sizes include eight framing bytes where marked.

| Nodes | Production JSON bytes | Cap’n Proto staging bytes | Canonical segment bytes | Canonical framed bytes |
| ---: | ---: | ---: | ---: | ---: |
| 4 | 2,388 | 832 | 824 | 832 |
| 65 | 33,864 | 11,080 | 11,072 | 11,080 |
| 512 | 257,006 | 75,952 | 71,728 | 71,736 |

### Steady state

BenchmarkDotNet 0.15.8, one launch, two warmups and five measured iterations targeting 100 ms. Each cell is median **µs/op / managed B/op**. Several measurements had short iterations and high variance, especially array-heavy 512-node operations. Treat ratios as directional. The lookup uses a known index and reads the ID length. Traversal visits each ID and its explicit prerequisites. The JSON DTO lookup and traversal start after deserialization. The file benchmarks use ordinary warm filesystem caching and include opening the file on every operation.

| Operation | 4 nodes | 65 nodes | 512 nodes |
| --- | ---: | ---: | ---: |
| JSON encode | 6.767402 / 12,016 | 138.520363 / 169,392 | 723.862823 / 1,285,584 |
| Cap’n Proto encode to staging segment | 1.427995 / 5,000 | 16.494855 / 67,552 | 110.833141 / 297,375 |
| Cap’n Proto canonicalize staging segment | 0.911208 / 2,984 | 11.800119 / 32,264 | 79.500307 / 229,144 |
| JSON decode to DTO | 43.464609 / 5,800 | 360.987461 / 78,664 | 2,298.123437 / 559,888 |
| Cap’n Proto validate and decode to DTO | 2.290474 / 3,864 | 80.679199 / 52,416 | 310.617229 / 389,008 |
| JSON decode and look up a node | 14.719157 / 5,800 | 211.885957 / 78,664 | 2,017.627734 / 559,888 |
| JSON DTO look up a node | 0.000955 / 0 | 0.001098 / 0 | 0.000963 / 0 |
| Cap’n Proto span look up a node | 0.056325 / 0 | 0.057443 / 0 | 0.056827 / 0 |
| JSON DTO full graph traversal | 0.008309 / 0 | 0.123783 / 0 | 1.087006 / 0 |
| Cap’n Proto span full graph traversal | 0.119617 / 0 | 3.050099 / 0 | 8.608849 / 0 |
| JSON SHA-256 and hex | 1.520929 / 208 | 16.234323 / 208 | 119.827471 / 208 |
| Cap’n Proto SHA-256 and hex | 0.843683 / 208 | 8.999638 / 208 | 34.241379 / 208 |
| JSON file open, read, decode and lookup | 42.734026 / 8,280 | 513.09538 / 112,621 | 2,686.74475 / 817,110 |
| Cap’n Proto file open, map and lookup | 11.055756 / 480 | 11.505316 / 480 | 14.214493 / 480 |

The framed payloads were 65–72% smaller than the production JSON. At 512 nodes, the stage medians correspond to about 1,380 JSON encodes/s, 9,020 binary staging encodes/s, 12,580 canonicalizations/s, 435 JSON DTO decodes/s and 3,220 validated binary DTO decodes/s. The binary write path needs both staging and canonicalization, plus framing when persisting a file. The largest win for file access was inspecting one node without creating hundreds of DTOs. Traversing a previously materialized JSON graph was faster than traversing the encoded binary at all three sizes.

### Cold process and peak memory

Each row is a fresh .NET child process. The first pipeline measures encode, hash and full decode, plus canonicalization and framing for Cap’n Proto. Recipe construction precedes that timer. Wall time includes process startup and setup. Peak RSS is the child’s entire working set, including the runtime and fixture. These are single observations per case with ordinary OS cache state.

| Nodes | Format | First pipeline ms | Process wall ms | Peak RSS MiB |
| ---: | --- | ---: | ---: | ---: |
| 4 | JSON | 132.079 | 242.518 | 48.49 |
| 4 | Cap’n Proto | 26.214 | 80.706 | 41.50 |
| 65 | JSON | 93.265 | 138.196 | 48.74 |
| 65 | Cap’n Proto | 26.257 | 76.176 | 41.99 |
| 512 | JSON | 96.472 | 145.024 | 50.98 |
| 512 | Cap’n Proto | 24.491 | 80.680 | 42.93 |

### NativeAOT smoke

The `linux-x64` warnings-as-errors publish and standalone run passed with zero trimming or AOT warnings. The 1,733,648-byte ELF executable encoded, canonicalized, hashed, decoded, accessed a build target and edge directly, and read a run node using a file mapping. The 416-byte framed smoke manifest has canonical SHA-256 `6f5d6caea27345dec88e6b1a564352bf53e17a2f20eb42a6418d23630a4a28a5`. The binary links to glibc and libm and needs the platform ELF loader. The .NET SDK, installed .NET runtime, and official Cap’n Proto binaries are unnecessary to run it. Windows, macOS, ARM64 and musl were untested.

## Reproduction

Run from the repository root with the .NET 10 SDK. The official reference checks require a C++ compiler, CMake, Ninja and a Cap’n Proto 1.2.0 installation. NativeAOT publishing on Linux also requires Clang and the system link dependencies.

```sh
capnp_work=$(mktemp -d)
git clone --depth 1 --branch v1.2.0 https://github.com/capnproto/capnproto.git "$capnp_work/ref"
cmake -S "$capnp_work/ref" -B "$capnp_work/build" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF -DWITH_OPENSSL=OFF -DWITH_ZLIB=OFF \
  -DCMAKE_INSTALL_PREFIX="$capnp_work/install"
cmake --build "$capnp_work/build"
cmake --install "$capnp_work/build"
export PATH="$capnp_work/install/bin:$PATH"
cmake -S CMakeWorkflow.Binary.Capnp/Interop -B "$capnp_work/oracle" -G Ninja \
  -DCMAKE_PREFIX_PATH="$capnp_work/install" -DCMAKE_BUILD_TYPE=Release
cmake --build "$capnp_work/oracle"

dotnet run -c Release -p:TreatWarningsAsErrors=true --project CMakeWorkflow.Binary.Capnp.Tests -- \
  "$capnp_work/install/bin/capnp" "$capnp_work/oracle/cmwf-capnp-oracle"
dotnet run -c Release -p:TreatWarningsAsErrors=true --project CMakeWorkflow.Binary.Capnp.Benchmarks -- \
  --filter '*' --artifacts "$capnp_work/bench"
dotnet run -c Release --project CMakeWorkflow.Binary.Capnp.Benchmarks -- --cold
dotnet publish CMakeWorkflow.Binary.Capnp.AotSmoke -c Release -r linux-x64 -warnaserror \
  -o "$capnp_work/aot"
"$capnp_work/aot/CMakeWorkflow.Binary.Capnp.AotSmoke"
ldd "$capnp_work/aot/CMakeWorkflow.Binary.Capnp.AotSmoke"
```

The managed-only test suite runs with no arguments. Supply both reference tool paths to require live interoperability checks. To reproduce the existing-library probe, build it in the temporary directory so it remains outside the prototype's dependency graph.

```sh
mkdir -p "$capnp_work/candidate"
cat > "$capnp_work/candidate/Candidate.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Capnp.Net.Runtime" Version="1.3.118"/>
  </ItemGroup>
</Project>
XML
cp CMakeWorkflow.Binary.Capnp/Interop/capnp-dotnetcore-probe.cs.txt "$capnp_work/candidate/Program.cs"
capnp encode CMakeWorkflow.Binary.Capnp/Schema/workflow.capnp Workflow \
  < CMakeWorkflow.Binary.Capnp.Tests/Fixtures/fixture.capnp.txt > "$capnp_work/fixture.bin"
dotnet publish "$capnp_work/candidate/Candidate.csproj" -c Release -r linux-x64 -warnaserror \
  -o "$capnp_work/candidate/native"
"$capnp_work/candidate/native/Candidate" "$capnp_work/fixture.bin"
```
