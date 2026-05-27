namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Displays help information about available commands.
/// </summary>
public class HelpCommand : CommandBase
{
    public HelpCommand(CommandContext context) : base(context) { }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        var helpText = @"
Jitzu Shell - Interactive REPL

Built-in Commands:
  cd [dir]       - Change directory (cd - for previous)
  ls [dir]       - List files and directories
  cat <file>     - Display file contents with line numbers
  head [-n] file - Show first N lines (default 10)
  tail [-n] file - Show last N lines (default 10)
  pwd            - Print working directory
  echo [text]    - Print text
  touch [-d/-t] f - Create file or set timestamp
  mkdir [-cd] d  - Create a directory (-cd to enter it)
  rm [-r] <path> - Remove file or directory
  mv <src> <dst> - Move or rename file/directory
  cp [-r] s d    - Copy file or directory
  which <cmd>    - Locate a command
  history        - Show command history
  grep [flags] pattern [file...] - Search files for pattern
                   -i  case insensitive
                   -n  show line numbers
                   -r  recursive search
                   -c  count matches only
  env            - Show environment variables
  export VAR=val - Set an environment variable
  unset VAR      - Remove an environment variable
  wc [-l/-w/-c] f - Count lines/words/chars in files
  sort [-r/-n/-u]  - Sort lines in a file
  uniq [-c/-d] f   - Remove consecutive duplicate lines
  find path [opts] - Recursive file search
                   -name pattern  filename pattern
                   -type f|d      files or directories
                   -ext .cs       filter by extension
  diff f1 f2      - Compare two files
  time <cmd>      - Measure command execution time
  watch [-n s] cmd - Repeat command every N seconds
  kill [-9] pid   - Kill a process by PID or %jobid
  kill [-9] -p n  - Kill process(es) listening on TCP port n
  killall [-9] n  - Kill all processes by name
  tee [-a] <file> - Write stdin to file(s) and stdout
  ln [-s] t link  - Create hard or symbolic link
  stat <file>     - Display file metadata
  chmod +/-r file - Toggle file attributes (r/h/s)
  whoami          - Print current user name
  hostname        - Print machine name
  uptime          - Show system uptime
  sleep <secs>    - Pause for N seconds
  yes [text]      - Repeat text (default: y)
  basename p [s]  - Strip directory (and optional suffix)
  dirname <path>  - Strip last component from path
  du [-sh] [dir]  - Disk usage of files/directories
  df              - Show disk space of mounted drives
  tr [-d] s1 s2 f - Translate or delete characters
  cut -d/-f/-c f  - Extract fields/chars from file
  seq [f [i]] last - Print sequence of numbers
  rev <file>      - Reverse characters in each line
  tac <file>      - Print file with lines in reverse order
  paste [-d] f f  - Merge lines from multiple files
  date [+fmt]     - Print current date/time (-u UTC, -I ISO)
  mktemp [-d]     - Create temporary file or directory
  true            - Return success (exit code 0)
  false           - Return failure (exit code 1)
  source <file>   - Execute a .jz file in the current session
  jobs            - List background jobs
  fg [%id]        - Bring background job to foreground
  bg              - List background jobs
  alias name=cmd - Define a persistent alias
  unalias name   - Remove an alias
  aliases        - List all aliases
  label n path   - Map a label to a path (e.g. label git D:/git)
  unlabel name   - Remove a path label
  labels         - List all path labels
  vars           - Show defined variables
  types          - Show available types
  functions      - Show defined functions
  reset          - Reset the session
  clear          - Clear the screen
  exit/quit      - Exit the shell
  monitor        - Full-screen activity monitor (CPU, RAM, processes)
  help           - Show this help message

Pipe Functions (use with | to filter command output):
  first           - First line of output
  last            - Last line of output
  nth(n)          - Nth line (0-indexed)
  grep(""pattern"") - Filter lines containing pattern
  head -n N       - First N lines (default 10)
  tail -n N       - Last N lines (default 10)
  sort [-r]       - Sort lines alphabetically
  uniq            - Remove consecutive duplicate lines
  wc [-l/-w/-c]   - Count lines/words/chars
  tee [-a] file   - Write to file(s) and pass through

Features:
  - Execute Jitzu code directly
  - Fall back to OS commands automatically
  - Command chaining: && (on success), || (on fail), ; (always)
  - Command substitution: $(command), e.g. echo $(pwd)
  - Background jobs: command &, then jobs/fg/bg
  - I/O redirection: > (write), >> (append), < (input)
  - Glob expansion: *.cs, src/**/*.js, test?.txt
  - Pipe OS commands into Jitzu functions (e.g. ls | grep(""cs""))
  - Environment variable expansion ($VAR, ${VAR})
  - Multi-line input (unclosed braces continue on next line)
  - Tab completion for variables, functions, and types
  - Command history (up/down arrows)
  - Script execution: dotnet run -- script.jz

Examples:
  let x = 42
  print(x + 1)
  type Person { pub name: String }
  export EDITOR=nvim
  echo $HOME
  ls -la                (falls back to OS command)
  ls | grep(""test"")      (filter OS output with Jitzu)
  ls | first            (first line of output)
  git log --oneline | nth(0)
  echo hello > file.txt && cat file.txt
  grep -rn ""TODO"" src/
  ls *.cs | sort
";

        return Task.FromResult(new ShellResult(ResultType.Jitzu, helpText, null));
    }
}
