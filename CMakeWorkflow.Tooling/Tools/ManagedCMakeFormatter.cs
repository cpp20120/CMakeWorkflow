using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling.Tools;

internal static class ManagedCMakeFormatter
{
    internal const string Id = "cmakefmt";
    internal const string Version = "2.4.0";

    private static readonly Version MinimumGlibc = new(2, 39);

    private static readonly ManagedToolRelease[] Releases =
    [
        Release("osx-arm64", "aarch64-apple-darwin",
            "a20c80159db8f5dbe32fc20e82a6840dbe9367d7b571a7a1f81a06070b6d16cc",
            "9f21070cce7d4c783f19445bf8de8184102d096e9bb01fcd088558354c186316"),
        Release("linux-arm64", "aarch64-unknown-linux-gnu",
            "9f7cd2e32e35c0e5360efd9aa711e8feedd3b9f5a18584f918c003ff63fa6029",
            "aa42ff9758daf7e09c348db6e6cb376749590eed02233f3ded263f9382b121ce"),
        Release("osx-x64", "x86_64-apple-darwin",
            "c3e20f5601877596cfdf238b0891de1bc073c0205c23011c84bce011617e871b",
            "17f928a2b21d5f81432d663ab8f74c55b69da16bdc4479e362b6c9f4d0a2b68d"),
        Release("win-x64", "x86_64-pc-windows-msvc",
            "9509f7253e3f5eaf235dd6f4d8f477026bd143d31b81e99b1ab9414e9a2b81d3",
            "ab7b330cc53471229500cc05042ed2ea33e009c273a8d29e169a1136c2f44fea"),
        Release("linux-x64", "x86_64-unknown-linux-gnu",
            "52c6f4032ed0c93fc9d46d48ffa5d56ccb844398a4dbdde384ccb619473c0a28",
            "e8a60e03513235a0f5ab2b7f570137a8d94a6a3ba80017d067285a182f697eff"),
        Release("linux-musl-x64", "x86_64-unknown-linux-musl",
            "eac9f9f81291032466de31a0e628fc17a87294bf5cf537d5dce7a788b59c5805",
            "6ed46caee9f8fb0eb0be1505fdd961f16ac1c34f1b827a4b157010abf27a92fe")
    ];

    internal static ManagedToolRelease? ForHost(ToolPlatform platform, Architecture architecture)
    {
        bool gnu = platform == ToolPlatform.Linux && System.Version.TryParse(ManagedLinuxSdk.ReadGlibcVersion(), out Version? glibc)
            && glibc >= MinimumGlibc;
        string? rid = (platform, architecture) switch
        {
            (ToolPlatform.Linux, Architecture.X64) => gnu ? "linux-x64" : "linux-musl-x64",
            (ToolPlatform.Linux, Architecture.Arm64) when gnu => "linux-arm64",
            (ToolPlatform.MacOS, Architecture.X64) => "osx-x64",
            (ToolPlatform.MacOS, Architecture.Arm64) => "osx-arm64",
            (ToolPlatform.Windows, Architecture.X64) => "win-x64",
            _ => null
        };
        return Releases.SingleOrDefault(release => release.Platform == rid);
    }

    private static ManagedToolRelease Release(string platform, string triple, string hash, string tree)
    {
        string root = $"{Id}-{Version}-{triple}";
        string extension = platform == "win-x64" ? ".zip" : ".tar.gz";
        return new(Id, Version, platform, $"https://github.com/cmakefmt/cmakefmt/releases/download/v{Version}/{root}{extension}",
            hash, tree, root);
    }
}
