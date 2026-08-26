using System.Diagnostics;

namespace Jitzu.Shell.Core;

/// <summary>
/// Extension methods for <see cref="Process"/> used across the shell.
/// </summary>
internal static class ProcessExtensions
{
    /// <summary>
    /// Suppresses Ctrl+C termination for the current shell process. Ctrl+C is delivered to
    /// the entire console process group, so an interactive child and the shell can receive
    /// it together. The child is deliberately left to handle the event; this handler only
    /// keeps the shell alive.
    /// </summary>
    private static readonly ConsoleCancelEventHandler SuppressCancelHandler = (_, args) => args.Cancel = true;

    public static IDisposable SuppressConsoleCancel()
    {
        Console.CancelKeyPress += SuppressCancelHandler;
        return new CancelSuppression();
    }

    public static async Task WaitForExitSuppressingCancelAsync(this Process process, CancellationToken cancellationToken = default)
    {
        using var suppression = SuppressConsoleCancel();
        await process.WaitForExitAsync(cancellationToken);
    }

    private sealed class CancelSuppression : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Console.CancelKeyPress -= SuppressCancelHandler;
        }
    }
}
