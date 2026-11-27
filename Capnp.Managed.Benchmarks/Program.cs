using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Capnp.Managed.Benchmarks;
using Perfolizer.Horology;

Job job = Job.Default.WithLaunchCount(1).WithWarmupCount(4).WithIterationCount(9)
    .WithIterationTime(TimeInterval.FromMilliseconds(150));
_ = BenchmarkSwitcher.FromAssembly(typeof(WireBenchmarks).Assembly).Run(args, DefaultConfig.Instance.AddJob(job));
