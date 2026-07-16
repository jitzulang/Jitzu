# Jitzu.Shell Performance Issues

## Completed

| # | Issue | Fix |
|---|-------|-----|
| 1 | Git subprocess spawned every prompt | `GitStatusCache` — repo root cached by directory, branch cached by .git/HEAD mtime |
| 2 | `new string(_buffer.ToArray())` per keystroke | `CollectionsMarshal.AsSpan` for return sites; zero-alloc span for hot paths |
| 3 | `HighlightBuffer` allocates StringBuilder per keystroke | Reused field-level `_highlightSb`, writes into caller's `ArrayBufferWriter` via `GetChunks` |
| 4 | Theme dictionary lookups in highlight loop | Already `FrozenDictionary` — no issue |
| 5 | Unbuffered `Console.Write` calls during render | Synchronized output (DEC private mode 2026) |
| 6 | History `LinkedList` O(n) index walk per arrow key | Replaced with `List<string>` for O(1) access; `SetBufferFromString` memcpy |
| 7 | `FindGitRepoFolder` directory walk every prompt | Solved by #1 |
| 8 | `GetGitBranch` double file read every prompt | Solved by #1 |
| 9 | PATH enumeration on every tab press | `_pathDirectoryCache` — file names cached per PATH directory, invalidated by directory mtime |
| 10 | Prompt builder allocates 3 StringBuilders + padding string every render | Single reusable `promptSb` cleared between uses; `cachedPadding` string reused when width unchanged |
| 11 | `RedrawLine` allocates new `ArrayBufferWriter<char>` every keystroke | Promoted to field-level `_redrawBuf` with `ResetWrittenCount()` — internal array reused across calls |
| 12 | Prompt blocks on `git status` subprocess | Stale-while-refreshing status cache; prompt rendering never awaits git |
| 13 | Independent startup I/O runs sequentially | Theme, runtime, history, and aliases initialize concurrently |
| 14 | Shell eagerly builds the language runtime and NuGet resolver | Runtime initialization is deferred until a Jitzu expression or runtime-aware completion needs it |
| 15 | Theme parsing creates a UTF-16 string and JSON DOM | Forward-only `Utf8JsonReader` parses bytes directly; 3.7% faster median startup in isolation |
| 16 | Config aliases rewrite the alias file once per command on every startup | Ignore unchanged aliases and batch config persistence into at most one write |
