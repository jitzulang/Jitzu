using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Jitzu.Shell;
using Jitzu.Shell.Infrastructure;
using Jitzu.Shell.Models;
using Jitzu.StartupBenchmark;
using Shouldly;

namespace Jitzu.Tests;

public class StartupSafetyTests
{
    [Test]
    public void StartupFileReader_RejectsContentBeyondLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-startup-limit-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, new byte[33]);
            Should.Throw<InvalidDataException>(() => StartupFileReader.ReadAllBytes(path, 32));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task OversizedHistory_BecomesReadOnlyAndIsPreserved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-history-limit-{Guid.NewGuid():N}");
        try
        {
            using (var stream = File.Create(path))
                stream.SetLength(StartupFileReader.HistoryMaxBytes + 1L);
            var history = new HistoryManager(true, path);

            history.Initialise();

            history.Count.ShouldBe(0);
            history.PersistenceWarning.ShouldNotBeNull();
            await Should.ThrowAsync<InvalidOperationException>(() => history.WriteAsync("must-not-replace"));
            new FileInfo(path).Length.ShouldBe(StartupFileReader.HistoryMaxBytes + 1L);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task OversizedAliases_BecomeReadOnlyAndArePreserved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-alias-limit-{Guid.NewGuid():N}");
        try
        {
            using (var stream = File.Create(path))
                stream.SetLength(StartupFileReader.AliasMaxBytes + 1L);
            var aliases = new AliasManager(true, path);

            aliases.Initialise();

            aliases.Aliases.ShouldBeEmpty();
            aliases.PersistenceWarning.ShouldNotBeNull();
            aliases.Set("new", "value");
            await Should.ThrowAsync<InvalidOperationException>(() => aliases.SaveAsync());
            new FileInfo(path).Length.ShouldBe(StartupFileReader.AliasMaxBytes + 1L);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task MissingPersistentFiles_AreNotCreatedUntilFirstWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-startup-lazy-{Guid.NewGuid():N}");
        var historyPath = Path.Combine(directory, "history.txt");
        var aliasPath = Path.Combine(directory, "aliases.txt");
        try
        {
            var history = new HistoryManager(true, historyPath);
            var aliases = new AliasManager(true, aliasPath);
            history.Initialise();
            aliases.Initialise();

            File.Exists(historyPath).ShouldBeFalse();
            File.Exists(aliasPath).ShouldBeFalse();

            await history.WriteAsync("echo first");
            aliases.Set("ll", "ls -la");
            await aliases.SaveAsync();
            File.ReadAllLines(historyPath).ShouldBe(["echo first"]);
            File.ReadAllLines(aliasPath).ShouldBe(["ll=ls -la"]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task ExternalHistoryGrowth_IsNeverOverwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-history-race-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "original\n");
            var history = new HistoryManager(true, path);
            history.Initialise();
            await File.AppendAllTextAsync(path, "external\n");

            await Should.ThrowAsync<IOException>(() => history.RemoveAsync("original"));
            (await File.ReadAllTextAsync(path)).ShouldBe("original\nexternal\n");
            history.PersistenceWarning.ShouldNotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ExternalAliasChange_IsNeverOverwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-alias-race-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "old=value\n");
            var aliases = new AliasManager(true, path);
            aliases.Initialise();
            await File.AppendAllTextAsync(path, "external=value\n");
            aliases.Set("new", "value");

            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("old=value\nexternal=value\n");
            aliases.PersistenceWarning.ShouldNotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task SameLengthAliasChange_WithRestoredTimestamp_IsDetected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-alias-digest-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "old=value\n");
            var timestamp = File.GetLastWriteTimeUtc(path);
            var aliases = new AliasManager(true, path);
            aliases.Initialise();
            await File.WriteAllTextAsync(path, "new=value\n");
            File.SetLastWriteTimeUtc(path, timestamp);
            aliases.Set("mine", "value");

            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("new=value\n");
            aliases.PersistenceWarning.ShouldNotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task AliasChange_DuringCommit_IsRestoredAndRejectedVersionIsPreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-cas-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "old=value\n");
            var aliases = new AliasManager(true, path,
                target => File.WriteAllText(target, "external=value\n"));
            aliases.Initialise();
            aliases.Set("mine", "value");

            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");
            Directory.GetFiles(directory, "*.rejected").Length.ShouldBe(1);
            var rejected = Directory.GetFiles(directory, "*.rejected")[0];
            (await File.ReadAllTextAsync(rejected)).ShouldContain("mine=value");
            if (OperatingSystem.IsWindows())
                AssertPrivateWindowsAcl(rejected);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task AliasChange_AfterAtomicReplace_IsDetectedAndNeverAccepted()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-post-cas-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "old=value\n");
            var aliases = new AliasManager(true, path, afterAtomicReplace:
                target => File.WriteAllText(target, "external=value\n"));
            aliases.Initialise();
            aliases.Set("mine", "value");

            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");
            aliases.PersistenceWarning.ShouldNotBeNull();
            aliases.Set("later", "must-not-overwrite");
            await Should.ThrowAsync<InvalidOperationException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");
            Directory.GetFiles(directory, "*.previous").ShouldBeEmpty();
            Directory.GetFiles(directory, "*.rejected").ShouldHaveSingleItem();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task AbsentAliasMutation_AfterMove_IsNotAcceptedAsCommittedState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-absent-post-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        try
        {
            var aliases = new AliasManager(true, path, afterSuccessfulCommit:
                target => File.WriteAllText(target, "external=value\n"));
            aliases.Initialise();
            aliases.Set("mine", "value");
            await aliases.SaveAsync();
            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");

            aliases.Set("later", "must-not-overwrite");
            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");
            aliases.PersistenceWarning.ShouldNotBeNull();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task ExistingAliasMutation_AfterVerification_IsNotAcceptedAsCommittedState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-verified-post-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "old=value\n");
            var aliases = new AliasManager(true, path, afterSuccessfulCommit:
                target => File.WriteAllText(target, "external=value\n"));
            aliases.Initialise();
            aliases.Set("mine", "value");
            await aliases.SaveAsync();
            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");

            aliases.Set("later", "must-not-overwrite");
            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");
            Directory.GetFiles(directory, "*.previous").ShouldBeEmpty();
            aliases.PersistenceWarning.ShouldNotBeNull();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Recovery_PreservesPostReplaceExternalContentUsingTheIntendedDigest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-post-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        try
        {
            const string intended = "mine=value\n";
            await File.WriteAllTextAsync(path, "external=value\n");
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(intended)));
            var backup = $"{path}.{Guid.NewGuid():N}.{digest}.previous";
            await File.WriteAllTextAsync(backup, "old=value\n");

            var aliases = new AliasManager(true, path);
            aliases.Initialise();

            (await File.ReadAllTextAsync(path)).ShouldBe("external=value\n");
            Directory.GetFiles(directory, "*.previous").ShouldBeEmpty();
            Directory.GetFiles(directory, "*.rejected").ShouldHaveSingleItem();
            (aliases.PersistenceWarning ?? string.Empty).ShouldContain("external change");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void MissingThemeDefaults_AreCreatedOnlyWhenTheLifecycleEnds()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"jitzu-theme-first-run-{Guid.NewGuid():N}");
        var path = Path.Combine(profile, ".jitzu", "colours.json");
        try
        {
            var theme = ThemeConfig.Load(loadUserConfig: true, userProfilePath: profile);
            File.Exists(path).ShouldBeFalse();

            theme.EnsureDefaultFile();

            File.Exists(path).ShouldBeTrue();
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            json.RootElement.GetProperty("prompt").GetProperty("arrow").GetString().ShouldBe("#5faf5f");
        }
        finally { if (Directory.Exists(profile)) Directory.Delete(profile, true); }
    }

    [Test]
    public void ConfiguredProfileGuard_DetectsAFileCreatedAfterAnAbsentSnapshot()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"jitzu-profile-guard-{Guid.NewGuid():N}");
        var config = Path.Combine(profile, "config.jz");
        var colours = Path.Combine(profile, "colours.json");
        Directory.CreateDirectory(profile);
        File.WriteAllText(colours, "{}");
        try
        {
            using var guard = new ProfileStateGuard("configured-user",
            [
                new StartupStateFile("config", config, CountExecutableLines: true),
                new StartupStateFile("colours", colours, CountExecutableLines: false)
            ]);
            File.WriteAllText(config, "echo external");

            Should.Throw<IOException>(() => guard.VerifyUnchanged());
        }
        finally { Directory.Delete(profile, true); }
    }

    [Test]
    public void NoPersistBenchmarkBoundary_ContainsOnlyConfigAndColours()
    {
        using var isolation = new BenchmarkIsolation("isolated");

        isolation.StartupStateFiles.Select(file => file.Label).ShouldBe(["config", "colours"]);
    }

    [Test]
    public void TraceSnapshot_IsPrivateWriterLockedAndRemovedOnDispose()
    {
        var source = Path.Combine(Path.GetTempPath(), $"jitzu-trace-source-{Guid.NewGuid():N}");
        File.WriteAllText(source, "private trace bytes");
        string snapshotPath;
        try
        {
            using (var snapshot = TraceSnapshot.Create(source))
            {
                snapshotPath = snapshot.Path;
                Should.Throw<IOException>(() =>
                {
                    using var _ = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite);
                });
                if (!OperatingSystem.IsWindows())
                    File.GetUnixFileMode(Path.GetDirectoryName(snapshot.Path)!).ShouldBe(
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.Exists(snapshotPath).ShouldBeFalse();
            Directory.Exists(Path.GetDirectoryName(snapshotPath)!).ShouldBeFalse();
        }
        finally { File.Delete(source); }
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public void ConPtyConstructorFailures_TerminateAndWaitForCreatedChildren()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        var executable = Path.Combine(systemRoot, "System32", "cmd.exe");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["ComSpec"] = executable,
            ["PATH"] = Path.Combine(systemRoot, "System32"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["TEMP"] = Path.GetTempPath(),
            ["TMP"] = Path.GetTempPath(),
            ["USERPROFILE"] = Path.GetTempPath(),
            ["HOME"] = Path.GetTempPath()
        };

        foreach (var failure in new[]
                 { ConPtyFailurePoint.Assign, ConPtyFailurePoint.Resume, ConPtyFailurePoint.PostCreate })
        {
            var processId = 0;
            Should.Throw<InvalidOperationException>(() =>
                _ = new ConPtyProcess(executable, "/d /c ping -n 30 127.0.0.1 >nul",
                    Path.GetTempPath(), environment, failure, id => processId = id));

            processId.ShouldBeGreaterThan(0);
            ProcessIsGoneOrExited(processId).ShouldBeTrue($"Injected {failure} child was not reaped.");
        }
    }

    private static bool ProcessIsGoneOrExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Test]
    public async Task HistoryChange_DuringRemove_IsRestored()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-history-cas-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "history.txt");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "original\n");
            var history = new HistoryManager(true, path,
                target => File.WriteAllText(target, "external\n"));
            history.Initialise();

            await Should.ThrowAsync<IOException>(() => history.RemoveAsync("original"));
            (await File.ReadAllTextAsync(path)).ShouldBe("external\n");
            Directory.GetFiles(directory, "*.rejected").Length.ShouldBe(1);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task OrdinaryAliasWriteFailure_LeavesOriginalAndAnnouncesReadOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-alias-failure-{Guid.NewGuid():N}");
        FileStream? blocker = null;
        try
        {
            await File.WriteAllTextAsync(path, "old=value\n");
            var aliases = new AliasManager(true, path,
                target => blocker = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None));
            aliases.Initialise();
            aliases.Set("mine", "value");

            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            blocker!.Dispose();
            blocker = null;
            (await File.ReadAllTextAsync(path)).ShouldBe("old=value\n");
            aliases.PersistenceWarning.ShouldNotBeNull();
        }
        finally { blocker?.Dispose(); File.Delete(path); }
    }

    [Test]
    public async Task OrdinaryHistoryAppendFailure_PreservesExistingBytesAndAnnouncesReadOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-history-failure-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "old\n");
            var history = new HistoryManager(true, path);
            history.Initialise();
            await using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Should.ThrowAsync<IOException>(() => history.WriteAsync("new"));

            (await File.ReadAllTextAsync(path)).ShouldBe("old\n");
            history.PersistenceWarning.ShouldNotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task PartialHistoryTemporaryWrite_PreservesOriginalAndAnnouncesReadOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-history-partial-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "history.txt");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "old\n");
            var history = new HistoryManager(true, path, temporaryWriter: async (temporary, content) =>
            {
                await File.WriteAllBytesAsync(temporary, content[..Math.Min(3, content.Length)].ToArray());
                throw new IOException("injected partial temporary write");
            });
            history.Initialise();

            await Should.ThrowAsync<IOException>(() => history.WriteAsync("new"));

            (await File.ReadAllTextAsync(path)).ShouldBe("old\n");
            Directory.GetFiles(directory, "*.tmp").ShouldBeEmpty();
            history.PersistenceWarning.ShouldNotBeNull();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task OrphanedAtomicBackup_ForcesReviewBeforeAnyFurtherWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "current=value\n");
            await File.WriteAllTextAsync($"{path}.{Guid.NewGuid():N}.previous", "preserved=value\n");
            var aliases = new AliasManager(true, path);
            aliases.Initialise();
            aliases.Set("mine", "value");

            await Should.ThrowAsync<InvalidOperationException>(() => aliases.SaveAsync());
            (await File.ReadAllTextAsync(path)).ShouldBe("preserved=value\n");
            Directory.GetFiles(directory, "*.rejected").Length.ShouldBe(1);
            (await File.ReadAllTextAsync(Directory.GetFiles(directory, "*.rejected")[0]))
                .ShouldBe("current=value\n");
            (aliases.PersistenceWarning ?? string.Empty).ShouldContain("rolled back");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task RestoreFailure_PreservesTheOnlyCopyOfExternalAliasBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-restore-failure-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        FileStream? blocker = null;
        try
        {
            await File.WriteAllTextAsync(path, "old=value\n");
            var aliases = new AliasManager(true, path,
                target => File.WriteAllText(target, "external=value\n"),
                target => blocker = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read));
            aliases.Initialise();
            aliases.Set("mine", "value");

            await Should.ThrowAsync<IOException>(() => aliases.SaveAsync());
            blocker!.Dispose();
            blocker = null;

            (await File.ReadAllTextAsync(path)).ShouldContain("mine=value");
            var backup = Directory.GetFiles(directory, "*.previous").ShouldHaveSingleItem();
            (await File.ReadAllTextAsync(backup)).ShouldBe("external=value\n");
            Directory.GetFiles(directory, "*.rejected").ShouldBeEmpty();
            aliases.PersistenceWarning.ShouldNotBeNull();
        }
        finally
        {
            blocker?.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task PersistentLoadIoFailure_DegradesWithoutAbortingStartupOrChangingBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"jitzu-alias-load-failure-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "preserve=value\n");
        try
        {
            await using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var aliases = new AliasManager(true, path);
                Should.NotThrow(aliases.Initialise);
                aliases.PersistenceWarning.ShouldNotBeNull();
                aliases.Set("mine", "value");
                await Should.ThrowAsync<InvalidOperationException>(() => aliases.SaveAsync());
            }

            (await File.ReadAllTextAsync(path)).ShouldBe("preserve=value\n");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task RejectedRetention_RemovesOnlyExpiredRecognizedRejectedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-rejected-retention-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "aliases.txt");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "current=value\n");
            var expired = $"{path}.{Guid.NewGuid():N}.rejected";
            var fresh = $"{path}.{Guid.NewGuid():N}.rejected";
            var unrelated = $"{path}.not-a-transaction.rejected";
            var previousOne = $"{path}.{Guid.NewGuid():N}.previous";
            var previousTwo = $"{path}.{Guid.NewGuid():N}.previous";
            foreach (var file in new[] { expired, fresh, unrelated, previousOne, previousTwo })
                await File.WriteAllTextAsync(file, Path.GetFileName(file));
            File.SetLastWriteTimeUtc(expired,
                DateTime.UtcNow - PersistentFileGuard.RejectedRetention - TimeSpan.FromHours(1));

            _ = new AliasManager(true, path);

            File.Exists(expired).ShouldBeFalse();
            File.Exists(fresh).ShouldBeTrue();
            File.Exists(unrelated).ShouldBeTrue();
            File.Exists(previousOne).ShouldBeTrue();
            File.Exists(previousTwo).ShouldBeTrue();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void NoConfigOption_DisablesConfigLoading()
    {
        JitzuOptions.Parse(["--no-config"]).Config.ShouldBeFalse();
    }

    [Test]
    public async Task StartupProfiler_EmitsEachMarkerOnce()
    {
        var startInfo = new ProcessStartInfo(ShellTestHarness.GetShellPath(),
            "--no-persist --no-splash --no-config")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["JITZU_STARTUP_PROFILE"] = "1";
        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteLineAsync("echo first");
        await process.StandardInput.WriteLineAsync("echo second");
        await process.StandardInput.WriteLineAsync("exit");
        process.StandardInput.Close();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        stderr.Split("input-ready").Length.ShouldBe(2);
        stderr.Split("input-accepted").Length.ShouldBe(2);
        stderr.Split("first-result-displayed").Length.ShouldBe(2);
        process.ExitCode.ShouldBe(0);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPrivateWindowsAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        security.AreAccessRulesProtected.ShouldBeTrue();
        var currentUser = WindowsIdentity.GetCurrent().User!.Value;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
            typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        rules.ShouldNotBeEmpty();
        rules.ShouldAllBe(rule => !rule.IsInherited
                                  && rule.AccessControlType == AccessControlType.Allow
                                  && rule.IdentityReference.Value == currentUser);
    }
}
