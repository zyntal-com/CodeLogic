# CodeLogic benchmarks

BenchmarkDotNet suite for the framework's hot paths: logging, the event bus and `Result<T>`.
It is not packed and not part of the CI test matrix; CodeLogic itself stays dependency-free.

```sh
dotnet run -c Release --project benchmarks/CodeLogic.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/CodeLogic.Benchmarks -- --filter '*Logging*'
```

Results land in `BenchmarkDotNet.Artifacts/` (git-ignored). Compare a change by running the
suite on the commit before it and on the change, and put both tables in the pull request.
