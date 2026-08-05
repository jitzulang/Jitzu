using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Jitzu.Shell.Infrastructure;

/// <summary>Content-addressed, failure-atomic protection for persistent startup state.</summary>
internal sealed class PersistentFileGuard
{
    internal static readonly TimeSpan RejectedRetention = TimeSpan.FromDays(7);
    private readonly string _path;
    private readonly Action<string>? _beforeAtomicReplace;
    private readonly Action<string>? _afterAtomicReplace;
    private readonly Action<string>? _afterSuccessfulCommit;
    private readonly Func<string, ReadOnlyMemory<byte>, Task>? _temporaryWriter;
    private byte[]? _digest;
    private bool _expectedToExist;

    public PersistentFileGuard(string path, bool enabled = true, Action<string>? beforeAtomicReplace = null,
        Action<string>? afterAtomicReplace = null, Action<string>? afterSuccessfulCommit = null,
        Func<string, ReadOnlyMemory<byte>, Task>? temporaryWriter = null)
    {
        _path = path;
        _beforeAtomicReplace = beforeAtomicReplace;
        _afterAtomicReplace = afterAtomicReplace;
        _afterSuccessfulCommit = afterSuccessfulCommit;
        _temporaryWriter = temporaryWriter;
        if (enabled)
        {
            try { CleanupRejectedFiles(DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Degrade($"expired rejected state could not be removed ({ex.GetType().Name})");
            }
            try { RecoverInterruptedReplace(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Degrade($"an incomplete atomic update could not be recovered ({ex.GetType().Name}); its backup was preserved");
            }

            try { CaptureCurrent(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Degrade($"persistent state could not be inspected ({ex.GetType().Name})");
            }
        }
    }

    public string? DegradedReason { get; private set; }
    public bool CanWrite => DegradedReason is null;
    public void Degrade(string reason) => DegradedReason ??= reason;

    public void VerifyUnchanged()
    {
        EnsureWritable();
        try
        {
            var (exists, digest) = ReadCurrent();
            if (exists != _expectedToExist || exists && !digest!.AsSpan().SequenceEqual(_digest))
                Conflict();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Degrade(DegradedReason ?? $"persistent state could not be verified ({ex.GetType().Name})");
            throw;
        }
    }

    public async Task ReplaceAtomicallyAsync(ReadOnlyMemory<byte> bytes)
    {
        EnsureWritable();
        var suffix = Guid.NewGuid().ToString("N");
        var intendedDigest = SHA256.HashData(bytes.Span);
        var temporary = $"{_path}.{suffix}.tmp";
        var backup = $"{_path}.{suffix}.{Convert.ToHexString(intendedDigest)}.previous";
        var rejected = $"{_path}.{suffix}.rejected";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            if (_temporaryWriter is null)
                await WriteTemporaryAsync(temporary, bytes);
            else
                await _temporaryWriter(temporary, bytes);

            VerifyUnchanged();
            _beforeAtomicReplace?.Invoke(_path);
            if (!_expectedToExist)
            {
                File.Move(temporary, _path, overwrite: false);
                SetCommittedState(intendedDigest);
                _afterSuccessfulCommit?.Invoke(_path);
                return;
            }

            File.Replace(temporary, _path, backup, ignoreMetadataErrors: true);
            _afterAtomicReplace?.Invoke(_path);
            if (!Digest(backup).AsSpan().SequenceEqual(_digest))
            {
                File.Replace(backup, _path, rejected, ignoreMetadataErrors: true);
                ProtectRejectedFile(rejected);
                Degrade("the file changed during save; external content was restored");
                throw new IOException($"Refusing to overwrite '{Path.GetFileName(_path)}' because it changed during save.");
            }

            if (!Digest(_path).AsSpan().SequenceEqual(intendedDigest))
            {
                // The installed target was changed after File.Replace. Keep that external
                // content active and retire the pre-save backup as protected review state.
                File.Move(backup, rejected, overwrite: false);
                ProtectRejectedFile(rejected);
                Degrade("the file changed after atomic replacement; external content was preserved");
                throw new IOException($"Refusing to accept '{Path.GetFileName(_path)}' because it changed after save.");
            }

            // From this point the successful commit is exactly the intended content.
            // Never re-read mutable target bytes into the expected state.
            SetCommittedState(intendedDigest);
            File.Delete(backup);
            _afterSuccessfulCommit?.Invoke(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Degrade(DegradedReason ?? $"an atomic update failed ({ex.GetType().Name})");
            throw;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task WriteTemporaryAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes);
        stream.Flush(true);
    }

    private void SetCommittedState(byte[] intendedDigest)
    {
        _expectedToExist = true;
        _digest = intendedDigest;
    }

    private void EnsureWritable()
    {
        if (DegradedReason is not null)
            throw new InvalidOperationException($"Persistent state is read-only: {DegradedReason}");
    }

    private void CaptureCurrent()
    {
        (_expectedToExist, _digest) = ReadCurrent();
    }

    private (bool Exists, byte[]? Digest) ReadCurrent()
    {
        try { return (true, Digest(_path)); }
        catch (FileNotFoundException) { return (false, null); }
        catch (DirectoryNotFoundException) { return (false, null); }
    }

    private void RecoverInterruptedReplace()
    {
        var fullPath = Path.GetFullPath(_path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
            return;

        var fileName = Path.GetFileName(fullPath);
        var prefix = fileName + ".";
        const string suffix = ".previous";
        var backups = Directory.EnumerateFiles(directory, $"{fileName}.*.previous")
            .Where(candidate => TryGetIntendedDigest(Path.GetFileName(candidate), prefix, suffix, out _))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (backups.Length == 0)
            return;
        if (backups.Length != 1)
        {
            Degrade($"{backups.Length} incomplete atomic updates have preserved backups requiring review");
            return;
        }

        var backup = backups[0];
        if (File.Exists(fullPath))
        {
            _ = TryGetIntendedDigest(Path.GetFileName(backup), prefix, suffix, out var intendedDigest);
            if (intendedDigest is not null && !Digest(fullPath).AsSpan().SequenceEqual(intendedDigest))
            {
                var preserved = $"{fullPath}.{Guid.NewGuid():N}.rejected";
                File.Move(backup, preserved, overwrite: false);
                ProtectRejectedFile(preserved);
                Degrade("a post-replacement external change was detected; current content was preserved");
                return;
            }

            var rejected = $"{fullPath}.{Guid.NewGuid():N}.rejected";
            File.Replace(backup, fullPath, rejected, ignoreMetadataErrors: true);
            ProtectRejectedFile(rejected);
        }
        else
        {
            File.Move(backup, fullPath, overwrite: false);
        }
        Degrade("an incomplete atomic update was rolled back; the interrupted version was preserved for review");
    }

    private static bool TryGetIntendedDigest(string name, string prefix, string suffix,
        out byte[]? intendedDigest)
    {
        intendedDigest = null;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        var transaction = name[prefix.Length..^suffix.Length];
        if (transaction.Length == 32 && transaction.All(Uri.IsHexDigit))
            return true; // Legacy transaction without a target digest.

        if (transaction.Length != 97 || transaction[32] != '.')
            return false;
        var id = transaction[..32];
        var digest = transaction[33..];
        if (!id.All(Uri.IsHexDigit) || !digest.All(Uri.IsHexDigit))
            return false;
        intendedDigest = Convert.FromHexString(digest);
        return true;
    }

    private void CleanupRejectedFiles(DateTime utcNow)
    {
        var fullPath = Path.GetFullPath(_path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
            return;

        foreach (var rejected in EnumerateTransactionFiles(fullPath, ".rejected"))
        {
            if (File.GetLastWriteTimeUtc(rejected) < utcNow - RejectedRetention)
                File.Delete(rejected);
            else
                ProtectRejectedFile(rejected);
        }
    }

    private static IEnumerable<string> EnumerateTransactionFiles(string fullPath, string suffix)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        var fileName = Path.GetFileName(fullPath);
        var prefix = fileName + ".";
        return Directory.EnumerateFiles(directory, $"{fileName}.*{suffix}")
            .Where(candidate =>
            {
                var name = Path.GetFileName(candidate);
                var transaction = name[prefix.Length..^suffix.Length];
                return transaction.Length == 32 && transaction.All(Uri.IsHexDigit);
            });
    }

    private void ProtectRejectedFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var user = WindowsIdentity.GetCurrent().User
                           ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
                var security = new FileSecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception privacyFailure) when (privacyFailure is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException
                                               or InvalidOperationException
                                               or PlatformNotSupportedException)
        {
            try { File.Delete(path); }
            catch (Exception deletionFailure) when (deletionFailure is IOException or UnauthorizedAccessException
                                                    or System.Security.SecurityException)
            {
                Degrade("a rejected state file could not be access-restricted or removed");
                throw new IOException("Rejected persistent state could not be secured or removed.",
                    new AggregateException(privacyFailure, deletionFailure));
            }
            Degrade("a rejected state file was discarded because private permissions could not be applied");
            throw new IOException("Rejected persistent state was discarded because private permissions could not be applied.",
                privacyFailure);
        }
    }

    private void Conflict()
    {
        Degrade("the file content changed after it was loaded");
        throw new IOException($"Refusing to overwrite '{Path.GetFileName(_path)}' because it changed after startup.");
    }

    private static byte[] Digest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
