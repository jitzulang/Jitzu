using System.Runtime.CompilerServices;
using Jitzu.Shell.UI.PromptPlugins;
using Shouldly;

namespace Jitzu.Tests;

public class PromptUpdateSessionTests
{
    [Test]
    public async Task PublishesEachPluginAsSoonAsItCompletes()
    {
        var fast = new ControlledPlugin("fast");
        var slow = new ControlledPlugin("slow");
        await using var session = new PromptUpdateSession(
            [fast, slow],
            new PromptContext("C:\\repo", "repo"),
            updates => string.Concat(updates.OrderBy(pair => pair.Key).Select(pair => pair.Value.Text)));

        fast.Publish("fast");
        (await WaitForPromptAsync(session)).ShouldBe("fast");

        slow.Publish("slow");
        (await WaitForPromptAsync(session)).ShouldBe("fastslow");
    }

    [Test]
    public async Task CoalescesUpdatesAndKeepsTheLatestValuePerPlugin()
    {
        var git = new ControlledPlugin("git");
        await using var session = new PromptUpdateSession(
            [git],
            new PromptContext("C:\\repo", "repo"),
            updates => updates["git"].Text);

        git.Publish("branch");
        git.Publish("branch *");

        (await WaitForPromptAsync(session, "branch *")).ShouldBe("branch *");
        session.TryGetPrompt(out _).ShouldBeFalse();
    }

    [Test]
    public async Task CancelsPluginWorkersWhenThePromptEnds()
    {
        var plugin = new ControlledPlugin("git");
        var session = new PromptUpdateSession(
            [plugin],
            new PromptContext("C:\\repo", "repo"),
            _ => "prompt");

        await session.DisposeAsync();

        await plugin.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<string> WaitForPromptAsync(PromptUpdateSession session)
        => await WaitForPromptAsync(session, expected: null);

    private static async Task<string> WaitForPromptAsync(PromptUpdateSession session, string? expected)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < timeout)
        {
            if (session.TryGetPrompt(out var prompt))
            {
                if (expected is null || prompt == expected)
                    return prompt;
            }
            await Task.Delay(5);
        }

        throw new TimeoutException("The prompt plugin did not publish an update.");
    }

    private sealed class ControlledPlugin(string id) : IPromptPlugin
    {
        private readonly System.Threading.Channels.Channel<PromptPluginUpdate> _updates =
            System.Threading.Channels.Channel.CreateUnbounded<PromptPluginUpdate>();

        public string Id { get; } = id;
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Publish(string text) => _updates.Writer.TryWrite(new PromptPluginUpdate(text));

        public async IAsyncEnumerable<PromptPluginUpdate> GetUpdatesAsync(
            PromptContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
                    yield return update;
            }
            finally
            {
                Cancelled.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            _updates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
