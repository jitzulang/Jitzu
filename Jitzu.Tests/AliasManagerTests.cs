using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class AliasManagerTests
{
    [Test]
    public void Set_Reports_Whether_Alias_Changed()
    {
        var aliases = new AliasManager(persist: false);

        aliases.Set("ll", "ls -la").ShouldBeTrue();
        aliases.Set("ll", "ls -la").ShouldBeFalse();
        aliases.Set("ll", "ls -l").ShouldBeTrue();
    }

    [Test]
    public async Task Save_Batch_Writes_Once_After_Outermost_Batch_Ends()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-alias-test-{Guid.NewGuid():N}");
        var aliasFile = Path.Combine(directory, "aliases.txt");
        var aliases = new AliasManager(persist: true, aliasFile);

        try
        {
            aliases.BeginSaveBatch();
            aliases.BeginSaveBatch();
            aliases.Set("ll", "ls -la");
            await aliases.SaveAsync();

            File.Exists(aliasFile).ShouldBeFalse();
            await aliases.EndSaveBatchAsync();
            File.Exists(aliasFile).ShouldBeFalse();
            await aliases.EndSaveBatchAsync();

            File.ReadAllLines(aliasFile).ShouldBe(["ll=ls -la"]);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
