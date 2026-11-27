using System.Globalization;
using System.Text;
using Capnp.Managed.CodeGen;
using Capnp.Managed.Schema;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
try
{
    if (args is not ([] or ["--check"]))
    {
        Console.Error.WriteLine("usage: capnpc-csharp [--check] < code-generator-request");
        return 2;
    }

    using Stream input = Console.OpenStandardInput();
    foreach (GeneratedFile source in CSharpGenerator.Generate(Bootstrap.Read(input)))
    {
        if (args.Length == 1)
        {
            byte[] expected = Encoding.UTF8.GetBytes(source.Text);
            FileInfo current = new(source.Name);
            if (!current.Exists || current.Length != expected.Length
                || !File.ReadAllBytes(source.Name).AsSpan().SequenceEqual(expected))
            {
                Console.Error.WriteLine($"capnpc-csharp: generated source differs: {source.Name}");
                return 1;
            }
        }
        else
        {
            File.WriteAllText(source.Name, source.Text, new UTF8Encoding(false));
        }
    }

    return 0;
}
catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or InvalidOperationException
                                  or UnauthorizedAccessException or OverflowException)
{
    Console.Error.WriteLine($"capnpc-csharp: {exception.Message}");
    return 1;
}
