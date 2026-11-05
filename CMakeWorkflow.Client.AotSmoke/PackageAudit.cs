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
            string contractsPath = Path.Combine(Path.GetDirectoryName(packagePath)!,
                "CMakeWorkflow.Contracts." + version + ".nupkg");
            using FileStream contractsStream = File.OpenRead(contractsPath);
            string contractsHash = Convert.ToBase64String(SHA512.HashData(contractsStream));
            contractsStream.Position = 0;
            using (ZipArchive contractsArchive = new(contractsStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                HashSet<string> requiredContracts = new(StringComparer.Ordinal)
                {
                    "_rels/.rels", "[Content_Types].xml", "CMakeWorkflow.Contracts.nuspec",
                    "lib/net10.0/CMakeWorkflow.Contracts.dll"
                };
                HashSet<string> seenContracts = new(StringComparer.Ordinal);
                foreach (ZipArchiveEntry entry in contractsArchive.Entries)
                {
                    if (!seenContracts.Add(entry.FullName))
                    {
                        throw new InvalidDataException("Duplicate Contracts package asset: " + entry.FullName);
                    }

                    if (!requiredContracts.Contains(entry.FullName) &&
                        !(entry.FullName.StartsWith("package/services/metadata/core-properties/",
                              StringComparison.Ordinal)
                          && entry.FullName.EndsWith(".psmdcp", StringComparison.Ordinal)))
                    {
                        throw new InvalidDataException("Unexpected Contracts package asset: " + entry.FullName);
                    }
                }

                if (!requiredContracts.IsSubsetOf(seenContracts))
                {
                    throw new InvalidDataException("Contracts package is missing required assets.");
                }

                using Stream contractsNuspec = contractsArchive.GetEntry("CMakeWorkflow.Contracts.nuspec")!.Open();
                XDocument contractsDoc = XDocument.Load(contractsNuspec);
                XNamespace contractsNs = contractsDoc.Root!.Name.Namespace;
                XElement contractsMetadata = contractsDoc.Root.Element(contractsNs + "metadata")!;
                if ((string?)contractsMetadata.Element(contractsNs + "id") != "CMakeWorkflow.Contracts"
                    || (string?)contractsMetadata.Element(contractsNs + "version") != version)
                {
                    throw new InvalidDataException("Unexpected Contracts package identity.");
                }

                if (contractsMetadata.Element(contractsNs + "dependencies") is not null &&
                    contractsMetadata.Element(contractsNs + "dependencies")!.Descendants(contractsNs + "dependency")
                        .Any())
                {
                    throw new InvalidDataException("Contracts package must have no package dependencies.");
                }
            }

            using FileStream packageStream = File.OpenRead(packagePath);
            string hash = Convert.ToBase64String(SHA512.HashData(packageStream));
            packageStream.Position = 0;
            using ZipArchive archive = new(packageStream, ZipArchiveMode.Read);
            HashSet<string> required = new(StringComparer.Ordinal)
            {
                "_rels/.rels", "[Content_Types].xml", "CMakeWorkflow.Client.nuspec",
                "lib/net10.0/CMakeWorkflow.Client.dll"
            };
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName;
                if (!seen.Add(name))
                {
                    throw new InvalidDataException("Duplicate package asset: " + name);
                }

                if (!required.Contains(name) && !(name.StartsWith("package/services/metadata/core-properties/",
                                                      StringComparison.Ordinal)
                                                  && name.EndsWith(".psmdcp", StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("Unexpected package asset: " + name);
                }
            }

            if (!required.IsSubsetOf(seen))
            {
                throw new InvalidDataException("Package is missing required assets.");
            }

            using Stream nuspecStream = archive.GetEntry("CMakeWorkflow.Client.nuspec")!.Open();
            XDocument document = XDocument.Load(nuspecStream);
            XNamespace ns = document.Root!.Name.Namespace;
            XElement metadata = document.Root.Element(ns + "metadata")!;
            if ((string?)metadata.Element(ns + "id") != "CMakeWorkflow.Client"
                || (string?)metadata.Element(ns + "version") != version)
            {
                throw new InvalidDataException("Unexpected package identity.");
            }

            XElement[] groups = [.. metadata.Element(ns + "dependencies")!.Elements(ns + "group")];
            if (groups.Length != 1 || (string?)groups[0].Attribute("targetFramework") != "net10.0")
            {
                throw new InvalidDataException("Unexpected target framework groups.");
            }

            XElement[] dependencies = [.. groups[0].Elements(ns + "dependency")];
            if (dependencies.Length != 3 ||
                !dependencies.Any(d => (string?)d.Attribute("id") == "StreamJsonRpc"
                                       && (string?)d.Attribute("version") == "2.26.150") ||
                !dependencies.Any(d => (string?)d.Attribute("id") == "SSH.NET"
                                       && (string?)d.Attribute("version") == "2026.0.0") ||
                !dependencies.Any(d => (string?)d.Attribute("id") == "CMakeWorkflow.Contracts"
                                       && (string?)d.Attribute("version") == version))
            {
                throw new InvalidDataException(
                    "Unexpected SDK dependencies; expected Contracts, StreamJsonRpc and SSH.NET.");
            }

            using JsonDocument assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
            JsonElement libraries = assets.RootElement.GetProperty("libraries");
            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() == "project")
                {
                    throw new InvalidDataException("Consumer restored a source project: " + library.Name);
                }
            }

            JsonElement contracts = libraries.GetProperty("CMakeWorkflow.Contracts/" + version);
            if (contracts.GetProperty("type").GetString() != "package" ||
                contracts.GetProperty("sha512").GetString() != contractsHash)
            {
                throw new InvalidDataException("Consumer did not restore the audited Contracts package bytes.");
            }

            JsonElement sdk = libraries.GetProperty("CMakeWorkflow.Client/" + version);
            if (sdk.GetProperty("type").GetString() != "package" || sdk.GetProperty("sha512").GetString() != hash)
            {
                throw new InvalidDataException("Consumer did not restore the audited SDK package bytes.");
            }

            foreach (JsonProperty target in assets.RootElement.GetProperty("targets").EnumerateObject())
            {
                JsonElement resolvedContracts = target.Value.GetProperty("CMakeWorkflow.Contracts/" + version);
                foreach (string? kind in new[] { "compile", "runtime" })
                {
                    JsonProperty[] entries = [.. resolvedContracts.GetProperty(kind).EnumerateObject()];
                    if (entries.Length != 1 || entries[0].Name != "lib/net10.0/CMakeWorkflow.Contracts.dll")
                    {
                        throw new InvalidDataException("Unexpected Contracts " + kind + " assets.");
                    }
                }

                JsonElement resolved = target.Value.GetProperty("CMakeWorkflow.Client/" + version);
                foreach (string? kind in new[] { "compile", "runtime" })
                {
                    JsonProperty[] entries = [.. resolved.GetProperty(kind).EnumerateObject()];
                    if (entries.Length != 1 || entries[0].Name != "lib/net10.0/CMakeWorkflow.Client.dll")
                    {
                        throw new InvalidDataException("Unexpected SDK " + kind + " assets.");
                    }
                }
            }

            Console.WriteLine(
                "PASS NuGet audit: Client + Contracts DLLs; StreamJsonRpc and SSH.NET dependencies; both package SHA512 match consumer; no source projects.");
            return 0;
        }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
        catch (Exception error)
        {
            Console.Error.WriteLine("NuGet audit failed: " + error.Message);
            return 1;
        }
#pragma warning restore CA1031

    }
}