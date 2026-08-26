using System.Runtime.CompilerServices;
using System.Text;

namespace Jitzu.Shell.UI.PromptPlugins;

internal sealed class GitPromptPlugin(ThemeConfig theme) : IPromptPlugin
{
    private readonly GitStatusCache _git = new();

    public string Id => "git";

    public async IAsyncEnumerable<PromptPluginUpdate> GetUpdatesAsync(
        PromptContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The session starts plugins on worker tasks, so all repository I/O stays off the
        // ReadLine thread. A cancelled prompt terminates an in-flight git process.
        var repository = _git.FindGitRepoFolder(context.WorkingDirectory);
        if (repository is null)
            yield break;

        var branch = _git.GetGitBranch(repository.FullName);
        if (branch is null)
            yield break;

        var displayDirectory = context.DisplayDirectory.Replace(repository.FullName, repository.Name);
        yield return new PromptPluginUpdate(BuildSuffix(branch), displayDirectory);

        var status = await GitStatusCache.GetGitStatusAsync(repository.FullName, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        yield return new PromptPluginUpdate(BuildSuffix(branch, status), displayDirectory);
    }

    private string BuildSuffix(string branch, GitStatus? status = null)
    {
        var suffix = new StringBuilder();
        suffix.Append($" {theme["git.branch"]}({branch}){ThemeConfig.Reset}");

        if (status is { } current && (current.HasDirty || current.HasStaged || current.HasUntracked))
        {
            suffix.Append(' ');
            if (current.HasDirty) suffix.Append($"{theme["git.dirty"]}*{ThemeConfig.Reset}");
            if (current.HasStaged) suffix.Append($"{theme["git.staged"]}+{ThemeConfig.Reset}");
            if (current.HasUntracked) suffix.Append($"{theme["git.untracked"]}?{ThemeConfig.Reset}");
        }

        if (status is { } remote && (remote.Ahead > 0 || remote.Behind > 0))
        {
            suffix.Append($" {theme["git.remote"]}");
            if (remote.Ahead > 0) suffix.Append($"↑{remote.Ahead}");
            if (remote.Behind > 0) suffix.Append($"↓{remote.Behind}");
            suffix.Append(ThemeConfig.Reset);
        }

        return suffix.ToString();
    }

    public ValueTask DisposeAsync() => _git.DisposeAsync();
}
