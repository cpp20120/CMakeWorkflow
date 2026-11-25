#:package CMakeWorkflow.Examples.NativeFragments@0.1.0-script.1
using CMakeWorkflow.Examples.NativeFragments;
using CMakeWorkflow.Scripting;

Console.WriteLine("Authoring the native workflow");
Workflow workflow = Workflow.Define("native-script", ".");
NativeBuildResult smoke = workflow.Scope("smoke").ConfigureBuildTest(new("gcc", "debug"));
if (args is ["--matrix"])
{
    string[] compilers = ["gcc", "clang"];
    string[] configurations = ["debug", "release"];
    IEnumerable<NodeRef> trials =
        from compiler in compilers
        from configuration in configurations
        where configuration == "release"
        let variant = new NativeVariant(compiler, configuration)
        let fragment = workflow.Scope("matrix").Scope(variant.Preset)
            .BenchmarkWorker(variant, "matrix.gcc-release", 3, [smoke.Test])
        from run in fragment.Trials
        select run;
    return await WorkflowScript.ExportAsync(workflow.Compose(trials));
}

return await WorkflowScript.ExportAsync(workflow.Compose([smoke.Test]));
