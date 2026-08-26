namespace Jitzu.Shell.UI.PromptPlugins;

/// <summary>
/// Runs prompt plugins concurrently and exposes coalesced prompt text to the ReadLine thread.
/// Plugin workers never write to the terminal.
/// </summary>
internal sealed class PromptUpdateSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PromptPluginUpdate> _updates = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyDictionary<string, PromptPluginUpdate>, string> _composePrompt;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task[] _pluginTasks;
    private bool _updatePending;

    public PromptUpdateSession(
        IReadOnlyList<IPromptPlugin> plugins,
        PromptContext context,
        Func<IReadOnlyDictionary<string, PromptPluginUpdate>, string> composePrompt)
    {
        _composePrompt = composePrompt;
        _pluginTasks = plugins
            .Select(plugin => Task.Run(
                () => RunPluginAsync(plugin, context, _cancellation.Token),
                CancellationToken.None))
            .ToArray();
    }

    public bool TryGetPrompt(out string prompt)
    {
        Dictionary<string, PromptPluginUpdate> snapshot;
        lock (_sync)
        {
            if (!_updatePending)
            {
                prompt = "";
                return false;
            }

            _updatePending = false;
            snapshot = new Dictionary<string, PromptPluginUpdate>(_updates, StringComparer.Ordinal);
        }

        // Composition may inspect terminal width and other UI state, so it stays entirely on
        // the ReadLine thread and outside the plugin coordination lock.
        prompt = _composePrompt(snapshot);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try
        {
            await Task.WhenAll(_pluginTasks).ConfigureAwait(false);
        }
        catch
        {
            // Plugins are best effort. Their failures must not affect command input or become
            // unobserved exceptions during session teardown.
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task RunPluginAsync(
        IPromptPlugin plugin,
        PromptContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in plugin.GetUpdatesAsync(context, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                lock (_sync)
                {
                    _updates[plugin.Id] = update;
                    _updatePending = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // A prompt plugin is optional decoration. A failure suppresses that plugin update
            // without interrupting ReadLine or other plugins.
        }
    }
}
