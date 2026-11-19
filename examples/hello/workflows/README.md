
`profile-windows-pmc.recipe.json` is a typed Windows ETW recipe. Generate a workflow with
`workflow generate profile --recipe examples/hello/workflows/profile-windows-pmc.recipe.json --output out/windows-pmc.json`
from the repository root, then execute on a Windows host with the corresponding configured build tree.
See [PMC profiling](../../../docs/en/profiling-and-campaigns.md#windows-pmc-overflow-counters) for prerequisites and units.
