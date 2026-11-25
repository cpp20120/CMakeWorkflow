using CMakeWorkflow.Examples.NativeFragments;
using CMakeWorkflow.Scripting;

if (args is not [string directory])
{
    await Console.Error.WriteLineAsync("Usage: Matrix WORKSPACE");
    return 2;
}

Workflow workflow = Workflow.Define("native-matrix", ".");
string[] compilers = ["gcc", "clang"];
string[] configurations = ["debug", "release"];
IEnumerable<NodeRef> trials =
    from compiler in compilers
    from configuration in configurations
    where compiler == "gcc" || configuration == "release"
    let variant = $"{compiler}-{configuration}"
    let fragment = workflow.Scope(variant).BenchmarkWorker(new(compiler, configuration), "gcc-debug", 3)
    from run in fragment.Trials
    select run;

WorkflowManifest manifest = workflow.Compose(trials);
Console.WriteLine(await manifest.SaveAsync(Path.GetFullPath(directory)));
Console.WriteLine(manifest.Sha256);
return 0;
