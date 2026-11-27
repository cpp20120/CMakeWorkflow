# ADR-0005 — Managed Cap’n Proto wire runtime

**2026-11-27 · Experimental decision**

[ADR-0006](0006-capnp-csharp-code-generation.md) records the generated consumers and source-generation workflow.

Extract a schema-independent wire library from the [P0 prototype](0004-capnp-persistence-feasibility.md). Keep the workflow schema, DTO projection, and normalization in the existing experimental adapter. Use the C++ implementation as a build-time interoperability oracle. Production persistence continues to use the established JSON, SQLite, and exact-byte identity contracts.

## Projects and dependency boundaries

| Project | Responsibility |
| --- | --- |
| `Capnp.Managed` | Wire words and addresses, unpacked framing, borrowed message and field readers, the single-segment arena, ownership, bounded validation, canonicalization, and hashing. It builds independently with no project references or runtime third-party dependencies. |
| `Capnp.Managed.Tests` | Managed property and mutation corpus, a hand-written schema adapter, and an explicitly required C++ oracle mode. |
| `Capnp.Managed.Benchmarks` | BenchmarkDotNet measurements of the generic runtime. |
| `Capnp.Managed.AotSmoke` | Standalone NativeAOT consumer of the wire library. |
| `CMakeWorkflow.Binary.Capnp` | Workflow-specific normalization, operation discrimination, DTO projection and views. The experimental tests, benchmarks, and AOT consumer continue to exercise it. |
| `capnp-experimental.yml` | A separate Linux and Windows CI workflow. Linux runs both official oracles. Both jobs are configured to build and execute the smoke programs. |

The runtime separates wire layout, message/framing, readers, builders, canonicalization and memory ownership. Full traversal is shared by validation and canonicalization so the two use identical pointer and budget rules. The workflow library uses the runtime's public API. Its `CapnpWire` helpers keep the existing unframed-segment entry points available, and the workflow view and decoder also accept a multi-segment `MessageReader`.

## Wire representation and API

`SegmentId`, `WordOffset`, and `WordAddress` describe physical locations. List indices, struct data bit offsets, and pointer-field indices belong to their reader or builder. A near pointer sign-extends its 30-bit relative offset from the word following the pointer. A single-far pointer resolves a one-word landing pad in a second segment. A double-far pointer resolves a two-word pad whose first word identifies the actual object location and whose second is a zero-offset struct or list tag. The runtime checks the pad and object ranges separately. [Official Cap’n Proto encoding](https://capnproto.org/encoding.html)

```csharp
var builder = new MessageBuilder();
var root = builder.InitRoot(new StructLayout(DataWords: 1, PointerWords: 1));
root.SetScalar(bitOffset: 0, width: 32, fieldValue: 31, defaultValue: 17);
root.GetPointer(0).SetText("example");

byte[] canonical = CanonicalMessage.CreateSegment(new MessageReader(builder.CopySegment()));
byte[] frame = FrameCodec.Write(new MessageReader(canonical));
string contentDigest = CanonicalMessage.HashSegment(canonical);
string storedDigest = CanonicalMessage.HashExactBytes(frame);

var reader = FrameCodec.Read(frame).Root.AsStruct();
uint number = (uint)reader.ReadScalar(bitOffset: 0, width: 32, defaultValue: 17);
ReadOnlySpan<byte> label = reader.GetPointer(0).AsText();
```

`MessageReader` also borrows a span of segment memories. A contiguous framed borrow of one segment has no prefix allocation. Multi-segment framed readers allocate one segment-offset table after checking the full header and lengths. Borrowed segmented memory uses the supplied descriptor array and has no runtime index allocation. `FrameCodec.Read(Stream)` reads one complete frame and leaves the stream positioned at the next frame. The span overload requires precisely one frame.

`PointerReader` provides struct, list, Text and Data access. Text validates UTF-8 and its terminator before returning a span. `DecodeText` explicitly materializes a string. Scalar access supports 1, 8, 16, 32 and 64 bits. Signed numbers, floats, enums and discriminants use their schema-specific bit representations. The reader applies the XOR default after extracting a field, including the zero storage for absent fields. A pointer accessor can use a separate immutable schema pointer when its own field is null.

`PointerBuilder`, `StructBuilder`, and `ListBuilder` are synchronous value handles to arena offsets. They survive arena growth. They expose neither raw arena references nor resizable-buffer spans. Allocation initializes padding and untouched data to zero. Replacing a pointer can leave unreachable staging words, which the canonical pass omits. `CopySegment` gives the caller a detached mutable byte array. `Snapshot` copies into a private immutable owner. Both can be called before continuing to build.

| Supported | Deliberate limits |
| --- | --- |
| Near, far and double-far pointers, null and empty structs, primitive, bit, pointer and inline composite lists, Text and Data | Reading arbitrary supported segment layouts. Builder allocation is single-segment. |
| Primitive and pointer lists read as struct prefixes and upgraded structs projected as compatible primitive or pointer lists | Bool lists retain their bit-list representation. Older projections require the corresponding first data or pointer section. Text and Data retain byte-list encoding. |
| Single- and multi-segment unpacked framing, readable padding, zeroed write padding | Packed encoding, capability pointers, reserved pointer kinds, RPC and schema parsing are outside the runtime. |
| Canonical copies preserve reachable data and unknown wire fields | A schema-specific DTO materialization can discard fields unknown to its adapter. The runtime does not normalize workflow semantics, schema scalar values, or collection ordering. |

## Ownership and validation

Borrowed readers and builder handles are ref structs. Safe code cannot store borrowed wire views in heap fields or return them through a scoped inspection delegate. Callers borrowing arrays, spans, or segmented descriptors must keep both the descriptors and bytes stable throughout their use. Independent concurrent reads of stable storage are supported. Builders require a single writer and external synchronization if shared through their owner. `OwnedMessage.CopyFrame` and `CopySegment` own private copies. The GC manages their memory. `OwnedMessage.ReadBuffered` bounds the frame before storing it privately and rejects truncation or trailing bytes.

`MappedMessage.Open` owns a disposable read-only mapping. `Inspect` synchronously pins the mapped view until the callback exits, including when another caller disposes the owner during inspection. New acquisitions after disposal fail. Use mappings only for an immutable file under the caller's control. An external writer that truncates an active mapping can fault at the OS level. Use the bounded buffered owner for potentially mutable files, and verify any required content identity on those privately owned bytes.

Local field access validates only the pointer chain, expected shape and bounds it follows. `CanonicalMessage.Validate` checks all reachable wire pointers within the selected policy. The validation cannot infer which unknown byte lists denote Text or Data, so those typed length limits and UTF-8 checks happen at typed access. The workflow DTO adapter performs a wire walk before materializing fields. DAG and filesystem checks remain with the planner.

`MessageLimits` controls message bytes, segment count and size, traversal work, nesting, element count, Text/Data bytes, canonical output bytes, and arena bytes. Defaults include a 64 MiB payload and per-segment/arena/output ceiling, 512 segments, 8,388,608 traversal units and 64 levels of nesting. The recursion ceiling is 128 even with custom limits. The runtime charges each reached object's footprint and each non-null pointer and landing pad. Every visit is charged again even when addresses alias or overlap, and lists of zero-width elements are charged by their logical element count. These are resource bounds rather than a global proof of unique object ownership. Cycles exhaust bounded nesting or work. Malformed stable bytes and exceeded wire limits produce `InvalidDataException`. API misuse such as an invalid element index produces an argument exception.

A validated immutable owner could cache the result of a wire walk under an exact resource policy. That alone would establish neither schema compatibility nor the interpretation of a particular byte list. P1 keeps the reader checks and avoids exposing unchecked views. Multi-segment owned and mapped inspections currently rebuild the framed offset index at each inspection. Batching work in a callback amortizes it.

## Canonicalization

The walk visits pointer children in preorder, materializes the canonical form in one segment, truncates trailing zero data and pointer words, uniformly trims inline composite struct widths, uses offset −1 for non-null empty structs, and clears primitive-list padding. It follows valid far landing pads and omits source segment boundaries, pads and unreachable arena words. Canonicalization is schema-independent. It distinguishes an absent/null pointer from a present empty value. `HashSegment` takes the unframed canonical bytes. `HashExactBytes` takes whichever exact bytes are being stored or transferred. [Official canonicalization rules](https://capnproto.org/encoding.html#canonicalization)

The C++ runtime generated the same fixture in one segment (216 framed bytes), ten segments with a far root (328 bytes), and five segments with a double-far Data pointer (296 bytes). Their canonical SHA-256 is `18957059d13e675d611102a9307e3af301b65cb509c56ccaf4874d164500ea07`. The managed builder emitted the same canonical bytes in two construction orders, and C++ verified all schema fields and canonicality of the managed frame. The workflow oracle also resegmented the original fixture and checked that the managed DTO projection and P0 canonical digest survived. The original P0 golden-vector, unknown-field, truncation, malformed-input and 10,000-case corpus checks still pass. The independent runtime suite adds 128 deterministic segment placements, full truncation sweeps, limit enforcement, builder growth and ownership checks, and 5,000 seeded malformed-frame or mutation cases. [Generic fixture](../../Capnp.Managed.Tests/Interop/probe.capnp) · [Independent oracle](../../Capnp.Managed.Tests/Interop/oracle.cpp) · [Generated-code example](../../Capnp.Managed.Tests/Probe.cs)

## Measurements

BenchmarkDotNet 0.15.8 on CachyOS Linux x64, AMD Ryzen 7 6800H, SDK 10.0.114, runtime 10.0.14. One launch, four warmups, nine target-150 ms measurement iterations. Each generic benchmark takes a fresh borrowed reader or an inspection callback as listed. The 512-entry graph has a 12,336-byte segment and a 12,344-byte frame. Near, far and double-far resolution use short equivalent scalar messages, with a 16-byte near segment, a 40-byte far frame, and a 48-byte double-far frame. Segmented far resolution uses caller-owned segment descriptors. The framed case includes building the checked offset table.

| Generic operation | Mean latency | Managed B/op |
| --- | ---: | ---: |
| Near pointer and scalar | 41.348 ns | 0 |
| Far pointer and scalar | 55.545 ns | 0 |
| Double-far pointer and scalar | 55.705 ns | 0 |
| Parse multi-segment frame and read scalar | 67.841 ns | 40 |
| Primitive access | 7.082 ns | 0 |
| Indexed struct-list access | 46.811 ns | 0 |
| Text span access | 52.020 ns | 0 |
| Traverse 512 fields | 1.485 µs | 0 |
| Validate graph | 4.707 µs | 0 |
| Build and grow arena | 10.916 µs | 37,440 |
| Canonicalize graph | 27.223 µs | 37,416 |
| Frame graph | 1.018 µs | 12,368 |
| Read owned frame | 49.924 ns | 0 |
| Inspect an open mapping | 56.162 ns | 0 |
| Open, map, inspect, dispose | 12.195 µs | 536 |

This corresponds to about 91,600 graph builds/s, 212,000 validations/s and 36,700 canonicalizations/s on this fixture. The near, far and double-far cases include creation of the borrowed reader and the callback used to read the scalar. The open-map measurement includes owning address-space and OS resources. The reused map and warmed file measurements use the ordinary OS cache.

### Workflow adapter versus P0

The 4-, 65-, and 512-node datasets are the P0 recipe fixtures. The 256-node fixture extends an 8-by-32 build matrix with up to 16 earlier prerequisites per node. The production JSON path was measured in the initial P1 run, and the binary cases below use the optimized rerun.

| Nodes | Explicit edges | JSON bytes | Binary staging bytes | Canonical segment bytes | Binary framed bytes |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | 3 | 2,388 | 832 | 824 | 832 |
| 65 | 64 | 33,864 | 11,080 | 11,072 | 11,080 |
| 256 | 3,960 | 233,024 | 53,872 | 51,760 | 51,768 |
| 512 | 0 | 257,006 | 75,952 | 71,728 | 71,736 |

Each cell is median **µs/op / managed B/op**. The file measurement includes open and disposal. The traversal counts each node ID and its explicit prerequisites. The JSON DTO traversal starts after deserialization.

| Operation | 4 nodes | 65 nodes | 256 nodes | 512 nodes |
| --- | ---: | ---: | ---: | ---: |
| JSON encode | 6.615 / 12,017 | 52.452 / 169,457 | 370.999 / 1,272,092 | 403.574 / 1,398,754 |
| Cap’n Proto staging encode | 1.588 / 5,000 | 17.894 / 67,552 | 253.598 / 523,733 | 106.836 / 297,375 |
| Cap’n Proto canonicalize | 1.176 / 2,984 | 14.104 / 32,264 | 76.424 / 233,457 | 108.702 / 229,149 |
| JSON decode to DTO | 14.657 / 5,800 | 211.268 / 78,668 | 1,163.000 / 576,496 | 1,775.000 / 559,982 |
| Cap’n Proto validate and decode to DTO | 3.817 / 3,864 | 50.057 / 52,416 | 227.739 / 267,824 | 351.753 / 389,008 |
| JSON decode and indexed lookup | 14.844 / 5,800 | 215.544 / 78,668 | 1,154.000 / 576,496 | 1,742.000 / 559,982 |
| JSON DTO indexed lookup | 0.000971 / 0 | 0.000884 / 0 | 0.000931 / 0 | 0.000922 / 0 |
| Cap’n Proto span indexed lookup | 0.157 / 0 | 0.156 / 0 | 0.155 / 0 | 0.153 / 0 |
| JSON DTO traversal | 0.008488 / 0 | 0.129 / 0 | 4.374 / 0 | 1.068 / 0 |
| Cap’n Proto span traversal | 0.508 / 0 | 7.051 / 0 | 221.532 / 0 | 29.435 / 0 |
| JSON SHA-256 and hex | 1.485 / 208 | 16.145 / 208 | 109.080 / 208 | 120.443 / 208 |
| Cap’n Proto SHA-256 and hex | 0.859 / 208 | 5.518 / 208 | 24.287 / 208 | 34.275 / 208 |
| JSON open, read, decode and lookup | 21.112 / 8,281 | 233.727 / 112,619 | 1,333.000 / 809,732 | 1,930.000 / 817,390 |
| Cap’n Proto open, map and lookup | 10.681 / 536 | 11.790 / 536 | 14.199 / 536 | 14.591 / 536 |

The common P0 fixtures have identical encoded sizes and canonical identities. The following medians show **P0 → P1** in µs/op. P0 used a shorter benchmark job and had greater variance on several array-heavy cases, so treat the changes as directional.

| Binary operation | 4 nodes | 65 nodes | 512 nodes |
| --- | ---: | ---: | ---: |
| Cap’n Proto staging encode | 1.428 → 1.588 | 16.495 → 17.894 | 110.833 → 106.836 |
| Cap’n Proto canonicalize | 0.911 → 1.176 | 11.800 → 14.104 | 79.500 → 108.702 |
| Cap’n Proto validate and decode to DTO | 2.290 → 3.817 | 80.679 → 50.057 | 310.617 → 351.753 |
| Cap’n Proto span indexed lookup | 0.056325 → 0.157 | 0.057443 → 0.156 | 0.056827 → 0.153 |
| Cap’n Proto span traversal | 0.120 → 0.508 | 3.050 → 7.051 | 8.609 → 29.435 |
| Cap’n Proto SHA-256 and hex | 0.844 → 0.859 | 9.000 → 5.518 | 34.241 → 34.275 |
| Cap’n Proto open, map and lookup | 11.056 → 10.681 | 11.505 → 11.790 | 14.214 → 14.591 |

At 512 nodes, the binary staging, canonicalization and full-decode rates were about 9,360, 9,200, and 2,840 operations/s. JSON encode and full-decode were about 2,480 and 560 operations/s. A binary write needs staging and canonicalization, plus framing for a file.

The first extraction regressed direct workflow traversal. Reducing reader-state copies, loading each pointer slot once, and reusing the checked adjacency row reduced the dense traversal median from 487.393 to 221.532 µs. The generalized lookup still incurs layout and segment checks, and its Text accessor validates UTF-8 on access. The P0 span accessor checked the terminator and left UTF-8 validation for string materialization. Dense traversal repeatedly resolves IDs in the encoded graph, while the deserialized DTO already holds those strings. The indexed and traversal paths still allocate zero managed bytes. Opening a mapping adds a disposable owner and costs 536 B/op versus 480 B/op in P0.

### NativeAOT

Both standalone Linux x64 executables published with warnings as errors and ran with zero trimming or AOT warnings. The generic ELF binary is 1,666,872 bytes and uses glibc, libm and the platform loader. The smoke constructs and frames a 16-byte canonical segment, reads single, far and double-far forms, and inspects a 48-byte mapped double-far frame. Its canonical SHA-256 is `9a8603f739d25a881287ef8c3fdad74c3ca1caa41983f09f83acf1506750b94e`. The workflow executable is 1,766,480 bytes and retains the P0 416-byte frame and `6f5d6caea27345dec88e6b1a564352bf53e17a2f20eb42a6418d23630a4a28a5` digest. Windows x64 is configured for actual execution in CI and was unavailable locally. Other platforms are untested.

## Reproduction

Run from the repository root on .NET 10. Managed tests run without native tools. For the required-oracle modes, build Cap’n Proto 1.2.0 using the [P0 reference installation commands](0004-capnp-persistence-feasibility.md#reproduction), which initialize `capnp_work` and add the reference tools to PATH.

```sh
dotnet build Capnp.Managed -c Release -warnaserror
dotnet run -c Release -p:TreatWarningsAsErrors=true --project Capnp.Managed.Tests
dotnet run -c Release -p:TreatWarningsAsErrors=true --project CMakeWorkflow.Binary.Capnp.Tests

cmake -S Capnp.Managed.Tests/Interop -B "$capnp_work/runtime-oracle" -G Ninja \
  -DCMAKE_PREFIX_PATH="$capnp_work/install" -DCMAKE_BUILD_TYPE=Release
cmake --build "$capnp_work/runtime-oracle"
dotnet run --no-build -c Release --project Capnp.Managed.Tests -- \
  --oracle "$capnp_work/runtime-oracle/capnp-managed-oracle"
dotnet run --no-build -c Release --project CMakeWorkflow.Binary.Capnp.Tests -- \
  "$capnp_work/install/bin/capnp" "$capnp_work/oracle/cmwf-capnp-oracle"

dotnet run -c Release --project Capnp.Managed.Benchmarks -- \
  --filter '*' --artifacts "$capnp_work/runtime-bench"
dotnet run -c Release --project CMakeWorkflow.Binary.Capnp.Benchmarks -- \
  --filter '*' --artifacts "$capnp_work/workflow-bench"

dotnet publish Capnp.Managed.AotSmoke -c Release -r linux-x64 -warnaserror -o "$capnp_work/runtime-aot"
"$capnp_work/runtime-aot/Capnp.Managed.AotSmoke"
dotnet publish CMakeWorkflow.Binary.Capnp.AotSmoke -c Release -r linux-x64 -warnaserror -o "$capnp_work/workflow-aot"
"$capnp_work/workflow-aot/CMakeWorkflow.Binary.Capnp.AotSmoke"
```

## P2 readiness

A compiler backend can consume the official compiler's emitted struct widths, field bit offsets, pointer indices, list element kinds and default encodings. It can generate small ref-struct wrappers around `StructReader` and `StructBuilder`, scalar calls with schema defaults, and nested or list views that keep borrowing the input. The hand-written `Probe` and workflow adapter exercise that model. The next phase still needs generator-to-oracle tests for union discriminants, group layout, list evolution and encoded pointer defaults. It can also evaluate whether a reusable immutable frame index and a long-lived mapped inspection session improve access patterns. Production decisions still require an immutable publication design and explicit compatibility and identity contracts.
