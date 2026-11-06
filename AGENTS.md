# Analyzer suppression policy

- Do not generate suppressions from build diagnostics or add them merely to obtain a passing build.
- A compatibility rationale must identify a concrete external contract and explain what would break. The external
  .NET package boundary is Client/Contracts; public visibility inside implementation assemblies is not such a contract.
- Naming an unspecified existing API or claiming that JSON serialization requires mutable List properties is insufficient.
- For implementation DTOs, use a reviewed member-local design exception or CA1002/CA2227 configuration naming exact
  source files. Document the actual producer, mutations or ownership. Do not use assembly-wide or directory-wide disables.
- Fix parameter-name mismatches, unnecessary public CLI entry points and ordinary cancellation-token ordering instead
  of attributing them to hypothetical callers. Distinguish connection lifetime tokens from per-call cancellation.
- Keep strict Release warnings-as-errors and NativeAOT checks enabled. After API changes, update the relevant inventory;
  verify serialization/recovery behavior when collection or persistence contracts are affected.
