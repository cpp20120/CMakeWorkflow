using System.Runtime.InteropServices;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Supply an existing absolute path to the SDK shared library.");
    return 2;
}
try
{
    NativeLibrary.SetDllImportResolver(typeof(Abi).Assembly, (_, _, _) => NativeLibrary.Load(args[0]));
    if (Abi.Create(40, out nint counter) != 0) return 3;
    try
    {
        if (Abi.Add(counter, 2, out uint value) != 0 || value != 42) return 4;
        Console.WriteLine("ABI round trip: 42");
    }
    finally
    {
        Abi.Destroy(counter);
    }
    return 0;
}
catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
{
    Console.Error.WriteLine("Cannot load the expected native ABI: " + error.Message);
    return 5;
}

internal static partial class Abi
{
    [LibraryImport("@NAME@", EntryPoint = "@NAME@_create")]
    internal static partial int Create(uint initial, out nint counter);
    [LibraryImport("@NAME@", EntryPoint = "@NAME@_add")]
    internal static partial int Add(nint counter, uint increment, out uint value);
    [LibraryImport("@NAME@", EntryPoint = "@NAME@_destroy")]
    internal static partial void Destroy(nint counter);
}
