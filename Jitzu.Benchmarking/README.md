# Jitzu benchmarks

Build Release before measuring:

```powershell
dotnet build Jitzu.slnx -c Release
```

Run the focused in-process and repository hot-path probes:

```powershell
dotnet run --project Jitzu.Benchmarking -c Release --no-build -- --hot-paths
```

This covers prompt Git status, cold and steady REPL expressions, history expansion,
and failure-atomic history persistence at several sizes. History persistence uses an
isolated temporary directory and does not touch the user's history.

Run the cross-language script suite, or select a workload and runtime:

```powershell
dotnet run --project Jitzu.Benchmarking -c Release --no-build
dotnet run --project Jitzu.Benchmarking -c Release --no-build -- --tests Empty --extensions jz
```

The harness automatically finds the repository and Release `jz.dll`. Use `--jitzu`
to target another built executable or DLL, and `--scripts` to target another workload
directory. Each workload receives two warmups by default and reports mean, median,
p95, standard deviation, and standard error. A nonzero process exit fails the run;
failed scripts are never accepted as timing samples.

Interactive startup-to-prompt A/B measurement is documented separately in
[`STARTUP.md`](STARTUP.md).
