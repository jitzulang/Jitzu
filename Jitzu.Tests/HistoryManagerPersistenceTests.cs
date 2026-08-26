using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class HistoryManagerPersistenceTests
{
    [Test]
    public async Task QueueWrite_DoesNotWaitForReplacement_AndFlushesTheLatestLogicalHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-history-queue-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "history.txt");
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;
        HistoryManager? history = null;

        async Task WriteTemporaryAsync(string temporary, ReadOnlyMemory<byte> content)
        {
            Interlocked.Increment(ref writeCount);
            writerStarted.TrySetResult();
            await releaseWriter.Task;
            await File.WriteAllBytesAsync(temporary, content.ToArray());
        }

        try
        {
            history = new HistoryManager(true, path, temporaryWriter: WriteTemporaryAsync);
            history.Initialise();

            // A blocked durable writer must not block the interactive record operation.
            await Task.Run(() => history.QueueWrite("first"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            history.QueueWrite("second");
            history.QueueWrite("first");
            history.Count.ShouldBe(2);

            releaseWriter.SetResult();
            await history.FlushAsync();

            File.ReadAllLines(path).ShouldBe(["second", "first"]);
            writeCount.ShouldBe(2);
        }
        finally
        {
            releaseWriter.TrySetResult();
            if (history is not null)
            {
                try { await history.FlushAsync(); }
                catch { }
            }

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task QueuedWrite_UsesTheSameExternalChangeGuardAsSynchronousWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-history-queue-race-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "history.txt");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(path, "original\n");
            var history = new HistoryManager(true, path,
                target => File.WriteAllText(target, "external\n"));
            history.Initialise();

            history.QueueWrite("new");
            await Should.ThrowAsync<IOException>(() => history.FlushAsync());

            (await File.ReadAllTextAsync(path)).ShouldBe("external\n");
            history.PersistenceWarning.ShouldNotBeNull();
            Directory.GetFiles(directory, "*.rejected").ShouldHaveSingleItem();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
