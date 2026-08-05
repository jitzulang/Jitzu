# Startup profile

Windows x64, .NET 10, measured 2026-08-05. The acceptance boundary is a Release, self-contained, single-file executable reaching a real ConPTY prompt, accepting a generated command, displaying its result, returning to a second prompt, and exiting zero.

## Provenance and reproduction

The true pre-loop baseline is `16e53a8ac1c12486a5e033e270071b51a45858a4`, the parent of the first startup commit `539d539`. Its `global.json` requested invalid SDK version `10.0.0`; publish selected installed SDK 10.0.302. The final production candidate is commit `aa8f70f` (full embedded commit `aa8f70f2415065f24043f3903bdf6fd8bfa8a34c`). Later changes, if any, contain evidence or documentation only.

The measured toolchain was SDK 10.0.302, MSBuild 18.6.11+35b593beb, host/runtime 10.0.10, Git 2.54.0.windows.1, `dotnet-trace` 9.0.661903+d7b455b46332b31fd9ba3a3f3e020387984c511a, TraceEvent 3.1.21, and Windows 10.0.26200 x64. The exact measured artifacts were:

- baseline: 92,268,551 bytes, SHA-256 `A0752D5A6490F0F720C48D70E4464D8D90C31AD21FD4563B5DC07633792DE45E`
- candidate: 92,284,423 bytes, SHA-256 `5E8DC92F0A8BE267D6FAF31C1F0B379518C49613BF10E3507552E2851317B371`

An equivalent candidate can be built with:

```powershell
dotnet publish Jitzu.Shell/Jitzu.Shell.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/startup
Get-FileHash publish/startup/jz.exe -Algorithm SHA256
```

A same-source republish produced different bytes under the same shell, so exact-byte reproducibility is not claimed. Commit, size, and SHA-256 are all required provenance; the benchmark and trace summaries bind those values while holding the executable read-locked. Commit arguments are explicitly labelled caller-supplied and are rejected unless they match the independently read embedded ProductVersion commit.

## Interactive acceptance method

The pre-loop shell predates startup instrumentation and does not support `--no-config`. Both artifacts therefore run with the common supported arguments `--no-persist --no-splash`, use the same real Windows UserProfile and ApplicationData locations, and load the same configured startup inputs. This measures configured startup, not clean-profile startup.

The harness resolves those Windows known folders once and supplies their exact values to both processes. It does not move, delete, rewrite, or patch user state, and no alternate token or sandbox is used. Under the common `--no-persist --no-splash` arguments, the integrity boundary contains only the startup-affecting config and colour files: aliases and history are neither read nor written and are deliberately excluded. Existing boundary files are held read-only with `FileShare.Read` for the complete run; missing boundary files are rechecked after every sample and at the end. This is not a claim that arbitrary mutations elsewhere in the profile are detected. The config was 837 bytes, SHA-256 `BADA77C8D94E8283B00A624CD58F1F1117BA028585E5087F1631FB9CAAF2E028`, with 16 executable lines; the colour file was 1,063 bytes, SHA-256 `A246F055122CCE2B61B11D6D1D6B50F5A954F5710422D1F9F9A95852DF367240`. Both were byte-identical afterward. Private inspection found only alias, export, label, and path directives, which mutate the child shell state but do not write files.

The harness detects the first rendered ConPTY prompt, sends a unique command, requires its echo and result, observes a second prompt, sends `exit`, and requires exit zero with no unexpected error. Candidate-only OSC markers diagnose managed phases but do not define the shared A/B boundary. Pair order is seeded and randomized. TEMP and working directory remain isolated. Current schema-5 reports retain hashes, byte counts, validation flags, markers, and timings—not paths, command lines, environment values, machine/user names, or terminal text. Historical evidence is separately audited and sanitized; this claim is not retroactively applied to every older schema.

The ConPTY runner creates each child suspended, assigns it to a kill-on-close Job Object, and then resumes it. If assignment fails while the child is still unassigned, it terminates and waits for that child directly. On timeout it terminates the job tree, waits for the root, verifies zero active processes, and records the result.

```powershell
dotnet run --project Jitzu.StartupBenchmark -c Release --no-build -- `
  --baseline "$env:TEMP\jitzu-cycle5-baseline-16e53a8\jz.exe" `
  --candidate "$env:TEMP\jitzu-cycle8-aa8f70f\jz.exe" `
  --baseline-commit 16e53a8ac1c12486a5e033e270071b51a45858a4 `
  --candidate-commit aa8f70f --sdk-version 10.0.302 `
  --warmups 5 --runs 80 --seed 1729 --timeout-seconds 60 `
  --readiness external-prompt --profile-mode configured-user `
  --shell-arguments "--no-persist --no-splash" `
  --output Jitzu.Benchmarking/results/startup-win-x64-16e53a8-vs-aa8f70f-configured.json
```

## Overall result

All 170 processes (five warmups and eighty measured runs per artifact) completed every validation step, and the configured files remained unchanged.

| Metric (ms) | Baseline median / p95 / max | Candidate median / p95 / max |
|---|---:|---:|
| First interactive prompt | 427.76 / 532.09 / 590.49 | 411.34 / 473.04 / 695.41 |
| Complete command round-trip | 460.07 / 577.85 / 630.41 | 446.49 / 503.68 / 747.50 |
| Candidate launch-to-managed aggregate | unavailable | 318.85 / 370.82 / 592.72 |
| Candidate application initialization | unavailable | 77.56 / 96.72 / 138.80 |

Prompt median improved 16.42 ms (3.84%) and p95 improved 11.10%; the paired candidate-minus-baseline median was -16.53 ms with 61/80 candidate wins. Round-trip median improved 13.58 ms (2.95%) and p95 improved 12.84%; the paired median was -25.38 ms with 64/80 wins. Candidate prompt maximum was 104.92 ms (17.77%) slower and round-trip maximum was 117.09 ms (18.57%) slower. The result is a central and p95 win with a material worst-case regression, not a uniform win.

The accepted evidence is [`results/startup-win-x64-16e53a8-vs-aa8f70f-configured.json`](results/startup-win-x64-16e53a8-vs-aa8f70f-configured.json). A deliberate one-millisecond timeout of the exact candidate recorded `processTreeTerminated: true`: [`results/startup-win-x64-16e53a8-vs-aa8f70f-timeout-attempt.timeout.json`](results/startup-win-x64-16e53a8-vs-aa8f70f-timeout-attempt.timeout.json).

The sole candidate maximum occurred at sample order 132: 695.41 ms to prompt, of which 592.72 ms was the launch-to-managed aggregate; application initialization was 85.83 ms and first render 31.39 ms. The next-largest measured launch aggregate was 387.49 ms. The one-sample pre-acceptance smoke also contained a 953.86 ms launch aggregate, but was diagnostic only. These observations locate the variability before managed entry but cannot distinguish apphost/bundle from CLR bootstrap. No repeat was run to seek a more favourable maximum.

The prior Cycle 5 report [`results/startup-win-x64-16e53a8-vs-29c3ee0-rejected.json`](results/startup-win-x64-16e53a8-vs-29c3ee0-rejected.json) is **REJECTED** and supports no acceptance claim. Its filename and machine-readable `Status`/`RejectionReason` make that disposition explicit. Its baseline resolved the real Windows profile and loaded the 837-byte, 16-line config plus real colours, while its candidate received an isolated empty profile. That configuration asymmetry invalidates the comparison. An N80 run against intermediate commit `1f673d7` was stopped when the rejected-persistence privacy gap was found; it produced no accepted report and was not rerun as a variant.

Incremental Cycle 4 evidence remains historical:

- [`results/startup-win-x64-0d3d83c-vs-b271369-symmetric.json`](results/startup-win-x64-0d3d83c-vs-b271369-symmetric.json): accepted incremental N80 comparison.
- [`results/startup-win-x64-0d3d83c-vs-26d9ee9.json`](results/startup-win-x64-0d3d83c-vs-26d9ee9.json) and [`results/startup-win-x64-0d3d83c-vs-6421f29.json`](results/startup-win-x64-0d3d83c-vs-6421f29.json): exploratory variants.
- [`results/startup-win-x64-0d3d83c-vs-b271369.json`](results/startup-win-x64-0d3d83c-vs-b271369.json): rejected asymmetric-marker run.

The Cycle 6 configured report [`results/startup-win-x64-16e53a8-vs-02748b9-configured.json`](results/startup-win-x64-16e53a8-vs-02748b9-configured.json) remains valid evidence for that exact artifact but is superseded because later review found exit, first-run theme, persistence, and evidence-hygiene blockers. Historical schema-1 report `startup-win-x64-539d539-vs-899c858.json` retains its numeric samples but has had command lines, absolute artifact paths, working directory, machine name, and user-identifying values removed.

The Cycle 7 configured report [`results/startup-win-x64-16e53a8-vs-3616709-configured.json`](results/startup-win-x64-16e53a8-vs-3616709-configured.json) remains valid evidence for that exact artifact but is superseded by the final persistence atomicity fixes.

## Managed phases and startup-suspended diagnostics

`JITZU_STARTUP_PROFILE=terminal` emits one-shot OSC markers with managed monotonic elapsed time. The benchmark accepts only an explicit startup-phase allowlist. `LaunchToManagedEntryMs` is the external prompt time minus managed entry-to-ready time; it is an aggregate of native apphost/bundle and CLR bootstrap. `ApplicationInitializationMs` spans parsed options through input readiness.

The exact final candidate's sanitized EventPipe summary is [`results/startup-aa8f70f-runtime-startup-summary.json`](results/startup-aa8f70f-runtime-startup-summary.json). It records RuntimeStart at 6.055 ms and managed entry at 60.307 ms on the same session clock, an interval of 54.253 ms, plus 526 CLR events including 180 JIT, 18 module, and 18 assembly events. The raw 602,046-byte trace has SHA-256 `9454F3337FD34CA4E3097A9108F4FA0A3C8CF1A4372C60AA1D81428BD8853685` and is not checked in because traces may contain private paths.

The native host summary is [`results/startup-aa8f70f-host-summary.json`](results/startup-aa8f70f-host-summary.json). It confirms apphost invocation, single-file detection, internal hostfxr selection, bundle startup, native extraction-directory setup, and managed execution. Its raw 236,530-byte trace has SHA-256 `1C223138A529721C36980A8C260856880CBFA97D70D7638935BFA0A54CB0DA80`. Host trace stages do not carry individual timestamps.

Both summarizers hold the source trace against writers, copy it into a current-user-only Windows directory (Unix mode 0700), keep the snapshot read-locked from hashing through parsing, and delete the private file and directory on disposal. Their schema-3 outputs embed the exact executable size/SHA-256, caller-supplied commit, embedded ProductVersion, and successful match result. Startup suspension adds a diagnostic handshake before RuntimeStart, so the capture cannot supply production launch-to-RuntimeStart timing. Kernel ETW/WPR could split the native interval, but `GeneralProfile.Light` was denied with `0xc5585011`; no synthetic subtraction is reported.

## Safety and lifecycle changes

The startup path defers updater cleanup until shutdown, avoids known-folder/theme resolution in no-config mode, resolves ANSI colours on demand, skips no-persist history work before the first result, and discovers completion PATH/PATHEXT lazily. First-run default `colours.json` creation is restored but deferred until the loaded command/REPL lifecycle ends, after the first interactive boundary; `CreateNew` preserves a file concurrently created by another session.

History and aliases snapshot SHA-256 content, write same-directory temporary files plus `File.Replace`, verify the captured version, and preserve exact external bytes across races. History appends now persist the complete logical history through that failure-atomic path, so an injected partial temporary write cannot damage the active file. Successful commits set the guard's expected state directly to the known intended digest and never recapture mutable target bytes; absent-target post-move and existing-target post-verification mutations are therefore detected on the next write and their external bytes are preserved. Atomic replacement names carry the intended target digest, and interrupted recognized transactions use it to distinguish rollback from an external post-commit change. `.previous` external-byte backups are never retention-deleted. Recognized `.rejected` artifacts are protected for the current user only (non-inherited Windows ACL, or Unix mode 0600), re-hardened when found fresh, and removed after seven days. If a rejected artifact cannot be secured or deleted, persistence degrades read-only. Unrecognized filenames are not retention-deleted.

The Windows updater durably stages replacements, rolls back failed installation, retains `.old` on rollback failure, and enumerates recognized numbered orphans across suffix gaps. Injected install-plus-rollback failure coverage verifies the `.old` copy remains. `exit`/`quit`, including aliases, short-circuit command chains and sourced input immediately. Top-level cleanup uses one outer `finally` for command, normal REPL, EOF, and elevated entry paths. Deterministic ConPTY assign, resume, and post-create failure injection verifies every created child is terminated and waited.

`dotnet build Jitzu.slnx -c Release` completed with zero warnings and errors. The repository's TUnit executable entry point, `dotnet run --project Jitzu.Tests/Jitzu.Tests.csproj -c Release --no-build -- --output Detailed`, passed 412/412 tests. A solution-level `dotnet test --no-build` invocation discovered zero tests under this runner and is not counted as test execution.

ReadyToRun, partial trimming, and externalizing native SQL SNI remain rejected because they respectively regressed startup or violated the one-file packaging contract.
