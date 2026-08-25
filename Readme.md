<h1 align="center">Jitzu</h1>

<div align="center">
  <a href="https://jitzu.dev/docs">Documentation</a>
  <span>&nbsp;&nbsp;•&nbsp;&nbsp;</span>
  <a href="https://github.com/jitzulang/jitzu/releases">Downloads</a>
  <span>&nbsp;&nbsp;•&nbsp;&nbsp;</span>
  <a href="https://github.com/jitzulang/jitzu/issues/new">Issues</a>
  <br />
</div>

<br>
<br>

Jitzu is a lightweight, expressive scripting language that runs on the .NET runtime, designed for speed, flexibility, and simplicity. It can be:

- **Interpreted** – Run scripts instantly with `jz script.jz`
- **Used as a Shell** – Execute commands interactively with `jz`
- **Extended with NuGet** – Import any .NET package directly
- **User-Defined Types** – Create custom types with fields and methods

## Installation

### Package Managers

```sh
# Windows (WinGet)
winget install --id JitzuLang.Jitzu --exact

# Windows (Scoop)
scoop bucket add jitzu https://github.com/jitzulang/jitzu
scoop install jz
```

### Manual Download

Download pre-built binaries from the [releases page](https://github.com/jitzulang/jitzu/releases):

| Platform | Download |
|----------|----------|
| Linux x64 | `jitzu-{version}-linux-x64.zip` |
| Windows x64 | `jitzu-{version}-win-x64.zip` |
| macOS x64 | `jitzu-{version}-osx-x64.zip` |
| macOS ARM | `jitzu-{version}-osx-arm64.zip` |

Extract the zip and add `jz` to your PATH.

### Self-Update

```sh
jz upgrade
```

## Quick Start

Run a script:
```sh
jz myscript.jz
```

Use Jitzu as an interactive shell:
```sh
jz
```

## Why Jitzu?

| Feature | Jitzu |
|---------|-------|
| Runtime | .NET 10 |
| Execution | Interpreted or Shell |
| NuGet Support | Load any .NET package |
| Syntax | Inspired by Rust, Go, Zig, C# |
| Philosophy | Code should be fun! |

## Example Code

```jitzu
// myscript.jz

fun add(x: Int, y: Int) -> Result<Int, String> {
    Ok(x + y)
}

let x = 10
let y = 20
let z = try add(x, y) or 30

`The sum of {x} and {y} is {z}`
```

Run it:
```terminal
$ jz myscript.jz
The sum of 10 and 20 is 30
```

## Jitzu Shell

The interactive shell supports:
- Command chaining (`|`, `&&`, `||`)
- I/O redirection (`>`, `>>`, `<`)
- Job control (background jobs, `fg`, `bg`)
- Glob expansion
- Tab completion
- Command recall (`^a0` inserts the first argument, `^l` the last, and `^p` the previous command)

```terminal
$ jz
jz v0.2.0
> echo "Hello, World!"
Hello, World!
> ls -la
...
> exit
```

## Documentation

Full documentation is available at [jitzu.dev/docs](https://jitzu.dev/docs).

## Building from Source

Requirements: .NET 10 SDK

```sh
# Build
dotnet build

# Run interpreter
dotnet run --project Jitzu.Shell -- ../Tests/script.jz

# Run shell
dotnet run --project Jitzu.Shell

# Run tests
dotnet test
```

## License

MIT License - see [LICENSE](LICENSE)
