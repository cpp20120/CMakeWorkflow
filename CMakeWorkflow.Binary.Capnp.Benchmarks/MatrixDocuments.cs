using System.Globalization;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Binary.Capnp.Benchmarks;

internal static class MatrixDocuments
{
    internal static WorkflowDocument Create(int size)
    {
        if (size is 256 or 512)
        {
            BuildVariantV1[] variants = [.. Enumerable.Range(0, size / 32).Select(i =>
                new BuildVariantV1(Name("v", i), BuildDirectory(Name("v", i)), "Release"))];
            string[] targets = [.. Enumerable.Range(0, 32).Select(i => Name("target", i))];
            WorkflowDocument matrix = BuildMatrixWorkflowBuilder.Build(new("build-matrix", 1, variants, targets));
            if (size == 256)
            {
                // A synthetic dense DAG built on the same production matrix nodes.
                for (int i = 1; i < matrix.Nodes.Count; i++)
                {
                    for (int dependency = Math.Max(0, i - 16); dependency < i; dependency++)
                    {
                        matrix.Nodes[i].DependsOn.Add(matrix.Nodes[dependency].Id);
                    }
                }
            }

            return matrix;
        }

        TestMatrixGroupV1[] groups = [.. Enumerable.Range(0, size - 1).Select(i =>
            new TestMatrixGroupV1(Name("g", i), null, Name("^unit", i) + "$"))];
        return TestMatrixWorkflowBuilder.Build(new("test-matrix", 1, BuildDirectory("tests"),
            "unit-tests", "Release", groups, "sequential", 30));
    }

    internal static int Traverse(WorkflowDocument document)
    {
        int sum = 0;
        foreach (WorkflowNode node in document.Nodes)
        {
            sum += node.Id.Length;
            foreach (string dependency in node.DependsOn)
            {
                sum += dependency.Length;
            }
        }

        return sum;
    }

    internal static int Traverse(WorkflowView view)
    {
        int sum = 0;
        for (int i = 0; i < view.NodeCount; i++)
        {
            NodeView node = view.GetNode(i);
            sum += node.IdUtf8.Length;
            DependencyListView dependencies = node.Dependencies;
            for (int j = 0; j < dependencies.Count; j++)
            {
                sum += view.GetNodeIdUtf8(dependencies.GetNodeIndex(j)).Length;
            }
        }

        return sum;
    }

    private static string Name(string prefix, int index) =>
        prefix + index.ToString("D3", CultureInfo.InvariantCulture);

    private static string BuildDirectory(string name) => Path.Combine(Path.GetTempPath(), "cmwf-matrix", name);
}
