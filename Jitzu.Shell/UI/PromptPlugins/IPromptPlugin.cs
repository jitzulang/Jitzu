namespace Jitzu.Shell.UI.PromptPlugins;

internal interface IPromptPlugin : IAsyncDisposable
{
    string Id { get; }

    IAsyncEnumerable<PromptPluginUpdate> GetUpdatesAsync(
        PromptContext context,
        CancellationToken cancellationToken = default);
}

internal readonly record struct PromptContext(string WorkingDirectory, string DisplayDirectory);

internal readonly record struct PromptPluginUpdate(string Text, string? DisplayDirectory = null);
