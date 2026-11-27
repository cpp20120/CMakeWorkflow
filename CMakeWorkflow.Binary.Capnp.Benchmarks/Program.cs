using CMakeWorkflow.Binary.Capnp.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

if (args is ["--first", string format, string size])
{
    ColdStart.Measure(format, int.Parse(size, System.Globalization.CultureInfo.InvariantCulture));
}
else if (args is ["--cold"])
{
    await ColdStart.RunAsync();
}
else
{
    Job job = Job.Default.WithLaunchCount(1).WithWarmupCount(4).WithIterationCount(9)
        .WithIterationTime(TimeInterval.FromMilliseconds(150));
    _ = BenchmarkSwitcher.FromAssembly(typeof(ManifestBenchmarks).Assembly)
        .Run(args, DefaultConfig.Instance.AddJob(job));
}
