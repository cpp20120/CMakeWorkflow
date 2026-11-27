# ADR-0006 — Cap’n Proto C# compiler backend

**2026-11-27 · Experimental decision**

Use Cap’n Proto 1.2.0 as the schema frontend. Generate strongly typed, borrowed C# readers and builders for the experimental workflow adapter. The generated consumers depend only on the P1 wire runtime. Production workflow persistence uses its established JSON, SQLite, and document identity contracts. [ADR-0004](0004-capnp-persistence-feasibility.md) · [ADR-0005](0005-managed-capnp-wire-runtime.md)

## Architecture and bootstrap

| Component | Responsibility |
| --- | --- |
| `Capnp.Managed` | Wire validation, offsets, framed and mapped borrowing, builders, canonicalization. P2 adds bounded pointer copying for immutable defaults and pointer constants. |
| `Capnp.Managed.Schema` → `Capnp.Managed` | Decode a compiler request into an immutable metadata model indexed by node ID. |
| `Capnp.Managed.CodeGen` → `Capnp.Managed.Schema` | Resolve types and imports, validate layouts and names, emit deterministic C#. |
| `Capnp.Managed.Compiler` → `Capnp.Managed.CodeGen` | Run as `capnpc-csharp` and consume the official unpacked binary request on stdin. Write generated sources to the current working directory. |
| `Capnp.Managed.Build` | Opt-in MSBuild targets for frontend invocation, generator invocation, incremental dependency tracking and generated-file inclusion. |
| Generated code → `Capnp.Managed` | Static wire layout and typed accessors. Includes the experimental workflow bindings and standalone consumer. |

`Bootstrap.cs` is the small checked-in reader for the 1.2.0 `CodeGeneratorRequest`. Its wire offsets come from the pinned `schema.capnp` and the official `capnpc-capnp` layout dump. Compiler metadata supplies the actual struct sizes, scalar and pointer offsets, ordinals, union discriminants, group layouts, type IDs, enumerants, and serialized default objects. The plugin checks the version, frame, node scopes, reference kinds, bounds and overlaps, discriminants, defaults, and naming collisions before writing any files. An imported constant may be folded by the frontend, so its requested-file import record can name a file node absent from the request. References to emitted types still require a resolvable declaration.

The bootstrap bounds each request to a 16 MiB message payload, 8,192 schema nodes, 128 requested files, 4,096 members per declaration, and the wire traversal and nesting budgets. Type and scope nesting are additionally bounded. Emitted text is capped at 16 Mi characters per file and 32 Mi characters per request. Unsupported requests produce a diagnostic on stderr and a nonzero status. [Official plugin protocol](https://capnproto.org/otherlang.html#how-to-write-compiler-plugins) · [Bootstrap](../../Capnp.Managed.Schema/Bootstrap.cs) · [Validation](../../Capnp.Managed.CodeGen/Analysis.cs)

The emitter generated and compiled bindings for the official `schema.capnp`. The required-oracle test used those bindings to read a real request and compared node kinds, names, counts, struct sizes, field offsets, types, and discriminants against the checked-in bootstrap. The plugin entry point still uses the small bootstrap. This establishes that generated protocol bindings can consume the request and provides a check for updating the bootstrap on a compiler version change.

## Generated API and language coverage

For the checked-in fixture, a standalone consumer can construct and inspect a message with only its generated source and the wire library. [Standalone NativeAOT example](../../Capnp.Managed.CodeGen.AotSmoke/Program.cs)

```csharp
using Capnp.Generated.Probe;
using Capnp.Managed;

MessageBuilder message = new();
Probe.Builder write = Probe.InitRoot(message);
write.SetU32(42);
write.InitPayload().InitDetails().InitInner().SetText("example");
write.InitSamples(2).Set(1, 7);

byte[] segment = CanonicalMessage.CreateSegment(new MessageReader(message.CopySegment()));
Probe.Reader read = Probe.ReadRoot(new MessageReader(segment));
ReadOnlySpan<byte> text = read.Payload.Details.Inner.TextUtf8;
foreach (ushort value in read.Samples)
{
    Console.WriteLine(value);
}
```

| Feature | Generated behavior |
| --- | --- |
| Void, Boolean, signed and unsigned integers, float, double, enum | Typed scalar members use compiler-provided offsets and XOR masks. Floating-point readers and writers reinterpret bits. Unknown enum values remain representable. |
| Text and Data | Borrowed spans, explicit Text string decoding, physical presence checks, pointer clear and set. Allocated empty values override a schema default. |
| Structs and lists | Typed struct, primitive, bit, enum, pointer, Text/Data, and nested list access. List views resolve once and support indexed access and pattern-based enumeration. Upgraded lists use the P1 runtime's compatible projections. |
| Groups and unions | Groups share the parent storage. Named and anonymous unions expose a typed discriminant. Inactive member access throws `InvalidOperationException`. Inspecting an unknown discriminant preserves its numeric value. |
| Defaults and constants | Scalars use official encoded bits. Pointer defaults are canonical copies of the compiler-provided objects, stored once per generated field in private arrays. Reading a default borrows it. Materializing it copies it into the destination arena. |
| Nested/imported declarations | The owning file provides the namespace. References are resolved by schema ID. Nested declarations and groups use flattened scoped names. |
| Unconstrained `AnyPointer` | A checked raw `PointerReader` and bounded copy into a builder. This is also used by the generated protocol bindings. |

Union transitions clear the storage owned by the union, including nested discriminants and obsolete pointers, before setting the new tag. Existing group handles should be used while their variant remains selected. Shared schema defaults expose only read operations. Ref-struct readers and builders borrow the runtime's storage and keep its bounds checks, UTF-8 validation, and lifetime restrictions. Use a scoped callback for mapped files and honor P1's immutable-file constraint.

Names are invariant Pascal case within `Capnp.Generated.<FileStem>`. Punctuation becomes underscores and nested names are joined by underscores. Generated helper names and runtime type names are reserved. Collisions, including case-insensitive filename collisions, fail with a diagnostic. Emit order is ordinal and generated files use UTF-8 without a BOM, LF newlines, a generated-source marker and nullable context. The emitter's file names derive from validated names. They carry no machine paths or timestamps.

The supported type subset is unbranded declarations plus unconstrained `AnyPointer`. Generic declarations, branded references, generic parameters, constrained or capability AnyPointer, interfaces and RPC are explicit errors. Annotations are decoded as metadata IDs. They do not customize C# naming or emit application attributes. Builder allocation is single-segment. The generator has no separate schema parser or layout engine.

## Verification and compatibility

- The full P0/P1 workflow suites passed with the generated adapter, including the original canonical SHA-256, the P1 resegmented workflow, unknown-field preservation, mapped access, and 10,000 adversarial cases. The generic P1 suite and 5,000 seeded frame cases also passed.
- The independent C++ oracle generated equivalent fixtures in one, 19 and four segments. The latter two exercise a far root and double-far Data pointer respectively. The generated reader agreed with the C++ data, and C++ accepted the managed canonical frame. The canonical segment is 496 bytes with SHA-256 `9729c70a8401a1d3a8938b65d8037c32f4c5e5c949138f9131cd413659ee8acf`.
- A scalar → pointer → group transition sequence yields the same canonical bytes as a fresh C++ build of the final value. Tests verify explicit negative zero, infinity, NaN payloads, and pointer defaults. The 1.2.0 C++ backend omits the XOR mask for a declared negative-zero default. The managed test checks that field against compiler metadata, and the cross-language negative-zero case uses an explicit value in a field with a positive-zero default.
- Versioned fixtures test adding scalar and pointer fields, an enum value and a union member, and upgrading a primitive list to a struct list. Both generated versions read reference messages and agree byte for byte with the reference canonicalizer for equivalent inputs. The old reader observes unknown numeric tags and the canonicalizer preserves the new wire fields. Wire compatibility does not imply generated API compatibility, domain equivalence, or unchanged hashes for populated additions.
- The standalone and workflow consumers published and executed locally as Linux x64 NativeAOT programs with zero trimming or AOT warnings. The compiler plugin also published as NativeAOT and successfully ran a generation check. Windows x64 consumer execution is configured in CI and was unavailable locally. The generated executable reads the fixture via shared runtime code and requires only the host system libraries after publish. [Experimental CI](../../.github/workflows/capnp-experimental.yml)

## MSBuild and checked-in generation

Checked-in bindings build and run without native compiler tools. The [opt-in targets](../../Capnp.Managed.Build/build/Capnp.Managed.targets) use explicit tool paths and a dedicated generated directory, normally under `obj`. MSBuild invokes the frontend with `-o-` and feeds the binary request to the selected generator. This also avoids relying on shell path resolution for the plugin.

```xml
<PropertyGroup>
  <CapnpEnableGeneration>true</CapnpEnableGeneration>
  <CapnpExecutable>/path/to/capnp</CapnpExecutable>
  <CapnpGeneratorExecutable>/path/to/capnpc-csharp</CapnpGeneratorExecutable>
  <CapnpSourcePrefix>$(MSBuildProjectDirectory)/Schema</CapnpSourcePrefix>
</PropertyGroup>
<ItemGroup>
  <CapnpSchema Include="Schema/workflow.capnp" />
  <CapnpImportDirectory Include="Schema/Imports" />
</ItemGroup>
<Import Project="/path/to/Capnp.Managed.Build/build/Capnp.Managed.targets" />
```

Use `CapnpSchemaDependency` for explicit dependencies outside the source prefix and import directory trees. Build inputs include schemas, imports under those trees, tool timestamps, generator sibling assemblies and the option/file membership manifest. Supply an absolute compiler path to track its timestamp. Design-time compilation can include previously generated outputs. The integration test verifies initial generation, multiple inputs, no-op rebuild, changing a non-requested import, stale output cleanup, missing output recovery and clean. The integration is a reusable targets file without public package release automation.

## Measurements

Benchmarks ran on CachyOS Linux x64, AMD Ryzen 7 6800H, .NET SDK 10.0.114 and runtime 10.0.14. BenchmarkDotNet 0.15.8 used one process launch, four warmups and nine target-150 ms measurement iterations.

### Same-message reader comparison

Each pair uses the same P1 checked wire access, field width/default and UTF-8 policy. Latencies are means. All reads and enumerations measured **0 managed B/op**.

| Operation | Hand-written layout | Generated layout |
| --- | ---: | ---: |
| Scalar with XOR default | 7.824 ns | 7.238 ns |
| Indexed struct and Text span | 99.762 ns | 105.099 ns |
| Union discriminant | 10.434 ns | 7.234 ns |
| Checked group and Text span | 71.933 ns | 46.492 ns |
| Enumerate 512 scalars | 2.390 µs | 4.688 µs |

The independent enumeration rerun measured 2.872 µs median for manual indexing, 3.669 µs for generated indexing, and 4.551 µs for generated `foreach`. Reusing the resolved typed list with an index recovered part of the difference. The pattern enumerator retains an additional wrapper and reads through its `Current` member, an overhead the runtime does not expose to the manual loop. The manual rerun had higher variance. Indexed loops are the current hot-path choice.

### Workflow adapter

The same P1 corpus and validation policies were used. The 256-node case has 3,960 explicit edges, and the 512-node case is a sparse build matrix. Encoded sizes matched P1, and the canonical golden hashes passed. Framed bytes for 4, 65, 256, and 512 nodes were 832, 11,080, 51,768, and 71,736. Production JSON used 2,388, 33,864, 233,024, and 257,006 bytes. The table shows median **P1 → P2 µs/op** and managed allocations for the larger P2 cases. Historical and P2 runs are separate measurement processes.

| Operation | 4 nodes | 65 nodes | 256 nodes | 512 nodes | B/op at 256 / 512 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Staging encode | 1.588 → 1.662 | 17.894 → 27.338 | 253.598 → 279.984 | 106.836 → 121.501 | 523,733 / 297,375 |
| Canonicalize | 1.176 → 1.211 | 14.104 → 14.229 | 76.424 → 78.758 | 108.702 → 121.184 | 233,457 / 229,149 |
| Validate and materialize DTO | 3.817 → 3.812 | 50.057 → 74.357 | 227.739 → 367.993 | 351.753 → 487.934 | 267,824 / 389,008 |
| Borrowed indexed lookup | 0.157 → 0.165 | 0.156 → 0.157 | 0.155 → 0.156 | 0.153 → 0.157 | 0 / 0 |
| Borrowed graph traversal | 0.508 → 0.551 | 7.051 → 8.353 | 221.532 → 286.723 | 29.435 → 30.017 | 0 / 0 |
| Hash and hex | 0.859 → 0.774 | 5.518 → 5.711 | 24.287 → 24.839 | 34.275 → 34.201 | 208 / 208 |
| Open, map, indexed lookup | 10.681 → 10.801 | 11.790 → 11.897 | 14.199 → 14.021 | 14.591 → 14.672 | 536 / 536 |

The 256-node graph reached about 2,717 binary DTO decodes/s. Its whole-graph traversal and DTO materialization regressed relative to P1 even though the borrowed paths stayed allocation-free. The generated adapter performs more typed field and union accessor calls during projection. At 512 nodes the borrowed indexed lookup and mapped lookup remained close to P1.

For comparison, the preserved production JSON benchmark took 1.163 ms and 1.775 ms to decode the 256- and 512-node fixtures. JSON file-open/read/decode/lookup took 1.333 ms and 1.930 ms respectively. Those JSON cases reuse the unchanged P1 baseline. DTO traversal starts with materialized strings and is faster than repeated resolution in the encoded graph.

The standalone generated consumer IL assembly is 30,208 bytes, and the generated workflow adapter IL assembly is 41,984 bytes. The standalone Linux x64 NativeAOT consumer is 1,695,552 bytes, and the native compiler plugin is 1,961,248 bytes. The workflow NativeAOT smoke grew from 1,766,480 bytes in P1 to 1,774,672 bytes in P2.

## Reproduction

Run from the repository root with .NET 10 and the [pinned 1.2.0 reference install from P0](0004-capnp-persistence-feasibility.md#reproduction). The commands assume `capnp_work` points to that reference workspace and `capnp` is on PATH.

```sh
dotnet build Capnp.Managed.Compiler -c Release -warnaserror
plugin="$PWD/Capnp.Managed.Compiler/bin/Release/net10.0/capnpc-csharp"
capnp compile --src-prefix=CMakeWorkflow.Binary.Capnp/Schema \
  "-o$plugin:CMakeWorkflow.Binary.Capnp/Generated" CMakeWorkflow.Binary.Capnp/Schema/workflow.capnp
capnp compile --src-prefix=CMakeWorkflow.Binary.Capnp/Schema -o- \
  CMakeWorkflow.Binary.Capnp/Schema/workflow.capnp > "$capnp_work/workflow.request"
(cd CMakeWorkflow.Binary.Capnp/Generated && "$plugin" --check < "$capnp_work/workflow.request")

mkdir -p "$capnp_work/protocol"
capnp compile --src-prefix="$capnp_work/install/include/capnp" "-o$plugin:$capnp_work/protocol" \
  "$capnp_work/install/include/capnp/schema.capnp"
cmake -S Capnp.Managed.CodeGen.Tests/Interop -B "$capnp_work/codegen" -G Ninja \
  -DCMAKE_PREFIX_PATH="$capnp_work/install"
cmake --build "$capnp_work/codegen"
dotnet run -c Release -p:TreatWarningsAsErrors=true --project Capnp.Managed.CodeGen.Tests \
  -p:CapnpProtocolBindings="$capnp_work/protocol/Capnp.Generated.Schema.g.cs" -- \
  --oracle "$capnp_work/install/bin/capnp" "$capnp_work/codegen/capnp-codegen-oracle"
dotnet run -c Release --no-build --project Capnp.Managed.CodeGen.Tests -- \
  --msbuild "$PWD" "$capnp_work/install/bin/capnp" "$plugin"

dotnet run -c Release --project Capnp.Managed.CodeGen.Benchmarks -- \
  --filter '*' --artifacts "$capnp_work/generated-bench"
dotnet run -c Release --project CMakeWorkflow.Binary.Capnp.Benchmarks -- \
  --filter '*ManifestBenchmarks.Binary*' --artifacts "$capnp_work/workflow-bench"
dotnet publish Capnp.Managed.CodeGen.AotSmoke -c Release -r linux-x64 -warnaserror -o "$capnp_work/generated-aot"
"$capnp_work/generated-aot/Capnp.Managed.CodeGen.AotSmoke"
dotnet publish Capnp.Managed.Compiler -c Release -r linux-x64 -p:PublishAot=true -warnaserror -o "$capnp_work/plugin-aot"
```

To regenerate the companion fixtures, invoke the plugin with `--src-prefix=Capnp.Managed.CodeGen.Tests/Schema`, output directory `Capnp.Managed.CodeGen.Tests/Generated`, and all four schema files from that schema directory. The required oracle compares them against the checked-in sources.

## P3 readiness

The generated workflow ABI, canonical encoding and hashes are stable on the verified fixtures. P3 can build the experimental immutable document store on this API. It still needs a publication and ownership design for mapped files, identity rules for stored frames versus canonical segments, and incremental architecture tests on representative storage workloads. The P2 access benchmarks make indexed iteration and whole-DTO materialization useful performance checkpoints for that phase.
