#:package CMakeWorkflow.Scripting@0.1.0-script.1
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Scripting;

Workflow workflow = Workflow.Define("format-build-test", ".");
ContextRef native = workflow.Context("native", "build", context => context.ConfigurePreset = "ninja");
NodeRef cppStyle = workflow.FormatCheck("cpp-style", native, FormatLanguage.Cpp);
NodeRef cmakeStyle = workflow.FormatCheck("cmake-style", native, FormatLanguage.CMake, managedOnly: true);
NodeRef configure = workflow.Configure("configure", native, after: [cppStyle, cmakeStyle]);
TargetBuildRef build = workflow.Build("build", native, "smoke", after: [configure]);
NodeRef test = workflow.CTest("tests", native, after: [build]);
return await WorkflowScript.ExportAsync(workflow.Compose([test]));
