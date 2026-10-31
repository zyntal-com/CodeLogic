using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// In-process toolchain: no separate host build per runtime moniker, which also keeps
// prerelease target frameworks (net11.0 before GA) working.
var config = DefaultConfig.Instance
    .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance).WithId("InProcess"));
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
