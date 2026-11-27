using System.Xml.Linq;

namespace Capnp.Managed.CodeGen.Tests;

internal static partial class Interop
{
    internal static void BuildIntegration(string repository, string compiler, string generator)
    {
        DirectoryInfo work = Directory.CreateTempSubdirectory("capnp-msbuild-");
        try
        {
            string root = work.FullName;
            string schemas = Path.Combine(root, "schemas");
            string imports = Path.Combine(root, "imports");
            _ = Directory.CreateDirectory(schemas);
            _ = Directory.CreateDirectory(imports);
            string dependency = Path.Combine(imports, "constants.capnp");
            string source = Path.Combine(schemas, "sample.capnp");
            string extra = Path.Combine(schemas, "extra.capnp");
            File.WriteAllText(dependency, "@0xa8e9816dd80f077e; const seed :UInt16 = 16;");
            File.WriteAllText(source,
                "@0x8f879cdc222f4549; using Constants = import \"/constants.capnp\"; struct Sample { n @0 :UInt16 = Constants.seed; }");
            File.WriteAllText(extra, "@0xbc075bc293a9ee58; struct Extra { flag @0 :Bool; }");
            string project = Path.Combine(root, "Integration.csproj");
            new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("CapnpSourcePrefix", schemas),
                    new XElement("CapnpEnableGeneration", "true"),
                    new XElement("CapnpExecutable", compiler),
                    new XElement("CapnpGeneratorExecutable", generator)),
                new XElement("ItemGroup",
                    new XElement("ProjectReference", new XAttribute("Include",
                        Path.Combine(repository, "Capnp.Managed", "Capnp.Managed.csproj"))),
                    new XElement("CapnpImportDirectory", new XAttribute("Include", imports)),
                    new XElement("CapnpSchema", new XAttribute("Include", source)),
                    new XElement("CapnpSchema", new XAttribute("Include", extra),
                        new XAttribute("Condition", "'$(IncludeExtra)' != 'false'"))),
                new XElement("Import", new XAttribute("Project",
                    Path.Combine(repository, "Capnp.Managed.Build", "build", "Capnp.Managed.targets")))).Save(project);

            _ = ReadProcess("dotnet", ["build", project, "-c", "Release", "-warnaserror"]);
            string output = Path.Combine(root, "obj", "Release", "net10.0", "capnp");
            string sampleBinding = Path.Combine(output, "Capnp.Generated.Sample.g.cs");
            string extraBinding = Path.Combine(output, "Capnp.Generated.Extra.g.cs");
            string before = File.ReadAllText(sampleBinding);
            DateTime stamp = File.GetLastWriteTimeUtc(sampleBinding);
            Require(File.Exists(extraBinding), "MSBuild ignored multiple requested schemas.");

            _ = ReadProcess("dotnet", ["build", project, "-c", "Release", "-warnaserror"]);
            Require(stamp == File.GetLastWriteTimeUtc(sampleBinding), "MSBuild regenerated an up-to-date binding.");

            File.WriteAllText(dependency, "@0xa8e9816dd80f077e; const seed :UInt16 = 31;");
            _ = ReadProcess("dotnet", ["build", project, "-c", "Release", "-warnaserror"]);
            Require(before != File.ReadAllText(sampleBinding), "Changing a non-requested import did not regenerate bindings.");

            File.Delete(sampleBinding);
            _ = ReadProcess("dotnet", ["build", project, "-c", "Release", "-warnaserror", "-p:IncludeExtra=false"]);
            Require(File.Exists(sampleBinding) && !File.Exists(extraBinding), "MSBuild did not recover missing or remove stale outputs.");

            _ = ReadProcess("dotnet", ["clean", project, "-c", "Release"]);
            Require(!File.Exists(sampleBinding), "MSBuild clean left a generated binding.");
            Console.WriteLine("MSBuild generation, import invalidation, no-op rebuild, stale cleanup and clean passed.");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }
}
