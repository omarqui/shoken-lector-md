using Shoken.Core.Model;

namespace Shoken.Core.Tests;

public class ReloadPolicyTests
{
    [Fact]
    public void Reload_IgnoresSaves_ThatDoNotChangeTheText() =>
        Assert.Equal(ReloadAction.Ignore, ReloadPolicy.Decide(false, true, true, true));

    [Fact]
    public void Reload_IsImmediate_WhenNothingIsPlaying() =>
        Assert.Equal(ReloadAction.ReloadNow, ReloadPolicy.Decide(true, false, false, false));

    [Fact]
    public void Reload_Asks_WhenPlayingWithTheWindowInFront() =>
        Assert.Equal(ReloadAction.Ask, ReloadPolicy.Decide(true, true, true, false));

    [Fact]
    public void Reload_WaitsForThePause_WhenPlayingWithTheWindowHidden() =>
        Assert.Equal(ReloadAction.WhenPaused, ReloadPolicy.Decide(true, true, false, false));

    [Fact]
    public void Reload_WaitsForTheNote_WhileANoteIsBeingWritten()
    {
        Assert.Equal(ReloadAction.WhenEditingEnds, ReloadPolicy.Decide(true, false, true, true));
        Assert.Equal(ReloadAction.WhenEditingEnds, ReloadPolicy.Decide(true, true, false, true));
    }
}
