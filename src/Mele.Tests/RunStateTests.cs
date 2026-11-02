using Mele.Engine.Orchestration;

namespace Mele.Tests;

public class RunStateTests
{
    [Fact]
    public void Round_trips_and_leaves_no_temp_file()
    {
        using var tmp = new TempDir();
        var f = Path.Combine(tmp.Path, "sub", "state.json");
        var s = new RunState();
        s.For("LE2").M3Confirmed = true;
        s.For("LE2").ContentSignature = "abc";
        s.Save(f);
        var back = RunState.Load(f).For("LE2");
        Assert.True(back.M3Confirmed);
        Assert.Equal("abc", back.ContentSignature);
        Assert.False(RunState.Load(f).For("LE1").TexturesInstalled);
        Assert.False(File.Exists(f + ".tmp"));
    }

    [Fact]
    public void Missing_or_corrupt_files_give_a_fresh_state_instead_of_blocking_a_run()
    {
        using var tmp = new TempDir();
        Assert.Empty(RunState.Load(Path.Combine(tmp.Path, "nope.json")).Games);
        var bad = Path.Combine(tmp.Path, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Empty(RunState.Load(bad).Games);
    }
}
