# Use CMakeWorkflow from a separate .NET application

Put the matching `CMakeWorkflow.Client` and `CMakeWorkflow.Contracts` packages from a release in a local NuGet feed.
The client package brings in Contracts, StreamJsonRpc and SSH.NET through NuGet. Run the CMakeWorkflow Host
executable on the machine that owns the workspace.

On Linux or macOS, start the Host using a private Unix socket directory:

```sh
host_dir=$(mktemp -d)
printf '%s\n' "$host_dir/rpc.sock"
CMakeWorkflow host serve --endpoint "$host_dir/rpc.sock"
```

On Windows, use a [named pipe](host-ipc.md#build-and-use).

In a separate directory, create a .NET 10 console project. Substitute the package version and absolute feed directory.

```sh
dotnet new console --framework net10.0 --no-restore
dotnet add package CMakeWorkflow.Client --version PACKAGE_VERSION --no-restore
dotnet restore --source ABSOLUTE_FEED_DIRECTORY --source https://api.nuget.org/v3/index.json
```

Replace `Program.cs` with the following:

```csharp
using CMakeWorkflow.Client;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("usage: SDK_DEMO HOST_ENDPOINT WORKSPACE [HISTORICAL_DIRECTORY]");
    return 2;
}

await using CMakeWorkflowClient client =
    await CMakeWorkflowClient.ConnectAsync(new(args[0], args[1]));
Console.WriteLine(string.Join(Environment.NewLine, client.Host.Capabilities));

if (args.Length == 3 && client.Host.Capabilities.Contains("profile/renderHistorical"))
{
    var report = await client.Profiles.RenderHistoricalAsync(new(args[2]));
    Console.WriteLine(report.IndexFile);
}

return 0;
```

```sh
dotnet run --no-restore -- HOST_ENDPOINT ABSOLUTE_WORKSPACE
```

Optionally pass the absolute path of an existing historical campaign inside the workspace as a third argument.
Paths returned by the SDK are on the Host. See the [public API inventory](../public-api.md) and the
[package-only NativeAOT smoke](../../CMakeWorkflow.Client.AotSmoke/README.md) for the SDK boundary and validation.
