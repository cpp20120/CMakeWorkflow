using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

internal static class PackageAudit
{
    // Audit the archive and the actual consumer restore graph, not just csproj text.
    public static int Run(string packagePath, string assetsPath, string version)
    {
        try
        {
            var contractsPath = Path.Combine(Path.GetDirectoryName(packagePath)!,
                "CMakeWorkflow.Contracts." + version + ".nupkg");
            using var contractsStream = File.OpenRead(contractsPath);
            var contractsHash = Convert.ToBase64String(SHA512.HashData(contractsStream));
            contractsStream.Position = 0;
            using (var contractsArchive = new ZipArchive(contractsStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                var requiredContracts = new HashSet<string>(StringComparer.Ordinal)
                {
                    "_rels/.rels", "[Content_Types].xml", "CMakeWorkflow.Contracts.nuspec",
                    "lib/net10.0/CMakeWorkflow.Contracts.dll"
                };
                var seenContracts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in contractsArchive.Entries)
                {
                    if (!seenContracts.Add(entry.FullName))
                        throw new InvalidDataException("Duplicate Contracts package asset: " + entry.FullName);
                    if (!requiredContracts.Contains(entry.FullName) &&
                        !(entry.FullName.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
                          && entry.FullName.EndsWith(".psmdcp", StringComparison.Ordinal)))
                        throw new InvalidDataException("Unexpected Contracts package asset: " + entry.FullName);
                }
                if (!requiredContracts.IsSubsetOf(seenContracts))
                    throw new InvalidDataException("Contracts package is missing required assets.");
                using var contractsNuspec = contractsArchive.GetEntry("CMakeWorkflow.Contracts.nuspec")!.Open();
                var contractsDoc = XDocument.Load(contractsNuspec);
                var contractsNs = contractsDoc.Root!.Name.Namespace;
                var contractsMetadata = contractsDoc.Root.Element(contractsNs + "metadata")!;
                if ((string?)contractsMetadata.Element(contractsNs + "id") != "CMakeWorkflow.Contracts"
                    || (string?)contractsMetadata.Element(contractsNs + "version") != version)
                    throw new InvalidDataException("Unexpected Contracts package identity.");
                if (contractsMetadata.Element(contractsNs + "dependencies") is not null &&
                    contractsMetadata.Element(contractsNs + "dependencies")!.Descendants(contractsNs + "dependency").Any())
                    throw new InvalidDataException("Contracts package must have no package dependencies.");
            }
            using var packageStream = File.OpenRead(packagePath);
            var hash = Convert.ToBase64String(SHA512.HashData(packageStream));
            packageStream.Position = 0;
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
            var required = new HashSet<string>(StringComparer.Ordinal)
            {
                "_rels/.rels", "[Content_Types].xml", "CMakeWorkflow.Client.nuspec",
                "lib/net10.0/CMakeWorkflow.Client.dll"
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (!seen.Add(name)) throw new InvalidDataException("Duplicate package asset: " + name);
                if (!required.Contains(name) && !(name.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
                    && name.EndsWith(".psmdcp", StringComparison.Ordinal)))
                    throw new InvalidDataException("Unexpected package asset: " + name);
            }
            if (!required.IsSubsetOf(seen)) throw new InvalidDataException("Package is missing required assets.");
            using var nuspecStream = archive.GetEntry("CMakeWorkflow.Client.nuspec")!.Open();
            var document = XDocument.Load(nuspecStream);
            var ns = document.Root!.Name.Namespace;
            var metadata = document.Root.Element(ns + "metadata")!;
            if ((string?)metadata.Element(ns + "id") != "CMakeWorkflow.Client"
                || (string?)metadata.Element(ns + "version") != version)
                throw new InvalidDataException("Unexpected package identity.");
            var groups = metadata.Element(ns + "dependencies")!.Elements(ns + "group").ToArray();
            if (groups.Length != 1 || (string?)groups[0].Attribute("targetFramework") != "net10.0")
                throw new InvalidDataException("Unexpected target framework groups.");
            var dependencies = groups[0].Elements(ns + "dependency").ToArray();
            if (dependencies.Length != 2 ||
                !dependencies.Any(d => (string?)d.Attribute("id") == "StreamJsonRpc"
                    && (string?)d.Attribute("version") == "2.26.150") ||
                !dependencies.Any(d => (string?)d.Attribute("id") == "CMakeWorkflow.Contracts"
                    && (string?)d.Attribute("version") == version))
                throw new InvalidDataException("Unexpected SDK dependencies; expected Contracts and StreamJsonRpc.");

            using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
            var libraries = assets.RootElement.GetProperty("libraries");
            foreach (var library in libraries.EnumerateObject())
                if (library.Value.GetProperty("type").GetString() == "project")
                    throw new InvalidDataException("Consumer restored a source project: " + library.Name);
            var contracts = libraries.GetProperty("CMakeWorkflow.Contracts/" + version);
            if (contracts.GetProperty("type").GetString() != "package" ||
                contracts.GetProperty("sha512").GetString() != contractsHash)
                throw new InvalidDataException("Consumer did not restore the audited Contracts package bytes.");
            var sdk = libraries.GetProperty("CMakeWorkflow.Client/" + version);
            if (sdk.GetProperty("type").GetString() != "package" || sdk.GetProperty("sha512").GetString() != hash)
                throw new InvalidDataException("Consumer did not restore the audited SDK package bytes.");
            foreach (var target in assets.RootElement.GetProperty("targets").EnumerateObject())
            {
                var resolvedContracts = target.Value.GetProperty("CMakeWorkflow.Contracts/" + version);
                foreach (var kind in new[] { "compile", "runtime" })
                {
                    var entries = resolvedContracts.GetProperty(kind).EnumerateObject().ToArray();
                    if (entries.Length != 1 || entries[0].Name != "lib/net10.0/CMakeWorkflow.Contracts.dll")
                        throw new InvalidDataException("Unexpected Contracts " + kind + " assets.");
                }
                var resolved = target.Value.GetProperty("CMakeWorkflow.Client/" + version);
                foreach (var kind in new[] { "compile", "runtime" })
                {
                    var entries = resolved.GetProperty(kind).EnumerateObject().ToArray();
                    if (entries.Length != 1 || entries[0].Name != "lib/net10.0/CMakeWorkflow.Client.dll")
                        throw new InvalidDataException("Unexpected SDK " + kind + " assets.");
                }
            }
            Console.WriteLine("PASS NuGet audit: Client + Contracts DLLs; StreamJsonRpc dependency; both package SHA512 match consumer; no source projects.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("NuGet audit failed: " + error.Message);
            return 1;
        }
    }
}
