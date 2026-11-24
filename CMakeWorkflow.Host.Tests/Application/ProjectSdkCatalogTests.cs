using System.Text.Json;
using CMakeWorkflow.CMake.Build;

namespace CMakeWorkflow.Host.Tests.Application;

internal static class ProjectSdkCatalogTests
{
    internal static void Run()
    {
        SdkDescriptor runtime = ProjectSdkCatalog.Resolve("runtime", "1.0.0");
        Require(ProjectSdkCatalog.Lineage(runtime).Select(sdk => sdk.Id)
            .SequenceEqual(["Native.Base", "Native.Library", "Native.Runtime"]), "SDK inheritance");
        SdkDescriptor next = runtime with { Version = "1.0.1" };
        SdkCatalogDocument source = ProjectSdkCatalog.Document;
        SdkCatalogDocument multiple = Parse(source with { Sdks = [next, .. source.Sdks] });
        Require(ProjectSdkCatalog.Resolve(multiple, "runtime", null).Version == "1.0.1" &&
                ProjectSdkCatalog.Resolve(multiple, "runtime", "1.0.0").Version == "1.0.0", "exact SDK version");

        Reject(() => Parse(source with { Sdks = [runtime, .. source.Sdks] }));
        Reject(() => Parse(source with
        {
            Sdks = [.. source.Sdks.Where(item => item.Id != runtime.Id),
            runtime with { Extends = new(runtime.Id, runtime.Version) }]
        }));
        Reject(() => Parse(source with { Sdks = [.. source.Sdks, next with { Extends = new("Native.Base", "0.0.0") }] }));
        Reject(() => Parse(source with { Sdks = [.. source.Sdks, next with { Scaffold = [new("../escape", "Native.cmake")] }] }));
        Reject(() => Parse(source with
        {
            Sdks = [.. source.Sdks, next with
        {
            Capabilities = next.Capabilities with { ExecutionModes = ["unrecognized-provider"] }
        }]
        }));
        Reject(() => ProjectSdkCatalog.Load("""{"schemaVersion":1,"schemaVersion":1,"sdks":[]}"""u8.ToArray()));
        Reject(() => ProjectSdkCatalog.Load("""{"schemaVersion":1,"sdks":[{"id":"Native.Broken"}]}"""u8.ToArray()));
        Console.WriteLine("SDK catalog versions, inheritance and invalid descriptor checks passed.");
    }

    private static SdkCatalogDocument Parse(SdkCatalogDocument catalog) => ProjectSdkCatalog.Load(
        JsonSerializer.SerializeToUtf8Bytes(catalog, ProjectSdkCatalogJson.Default.SdkCatalogDocument));

    private static void Reject(Func<SdkCatalogDocument> action)
    {
        try
        {
            _ = action();
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            return;
        }
        throw new InvalidOperationException("Catalog accepted an invalid descriptor.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
    }
}
