using BenchmarkDotNet.Attributes;
using CodeLogic.Core.Results;

namespace CodeLogic.Benchmarks;

[MemoryDiagnoser]
public class ResultBenchmarks
{
    private readonly Result<int> _ok = 42;
    private readonly Result<int> _fail = Error.NotFound("bench.missing", "missing");

    [Benchmark(Baseline = true)]
    public int IsSuccess_and_Value() => (_ok.IsSuccess ? _ok.Value : -1) + (_fail.IsSuccess ? _fail.Value : -1);

    // Union pattern matching goes through TryGetValue: no boxing for value-type T.
    [Benchmark]
    public int Pattern_match()
    {
        var a = _ok switch { int n => n, Error => -1, null => 0 };
        var b = _fail switch { int n => n, Error => -1, null => 0 };
        return a + b;
    }
}
