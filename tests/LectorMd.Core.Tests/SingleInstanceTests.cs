using LectorMd.Core.Ipc;

namespace LectorMd.Core.Tests;

public class SingleInstanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string UniqueName() => "LectorMdPrueba-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SecondClaim_Fails_WhileTheFirstIsAlive()
    {
        string name = UniqueName();
        using (SingleInstanceChannel? first = SingleInstanceChannel.TryClaim(name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceChannel.TryClaim(name));
        }

        using SingleInstanceChannel? again = SingleInstanceChannel.TryClaim(name);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Send_DeliversThePath()
    {
        string name = UniqueName();
        using SingleInstanceChannel channel = SingleInstanceChannel.TryClaim(name)!;
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.MessageReceived += message => received.TrySetResult(message);

        Assert.True(await SingleInstanceChannel.SendAsync(name, @"C:\Mis docs\guía.md", Timeout));
        Assert.Equal(@"C:\Mis docs\guía.md", await received.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Send_EmptyMeansShow()
    {
        string name = UniqueName();
        using SingleInstanceChannel channel = SingleInstanceChannel.TryClaim(name)!;
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.MessageReceived += message => received.TrySetResult(message);

        Assert.True(await SingleInstanceChannel.SendAsync(name, null, Timeout));
        Assert.Null(await received.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Send_HandlesSeveralMessagesInARow()
    {
        string name = UniqueName();
        using SingleInstanceChannel channel = SingleInstanceChannel.TryClaim(name)!;
        var messages = new List<string?>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.MessageReceived += message =>
        {
            lock (messages)
            {
                messages.Add(message);
                if (messages.Count == 3)
                {
                    done.TrySetResult();
                }
            }
        };

        foreach (string path in new[] { "a.md", "b.md", "c.md" })
        {
            Assert.True(await SingleInstanceChannel.SendAsync(name, path, Timeout));
        }

        await done.Task.WaitAsync(Timeout);
        Assert.Equal(["a.md", "b.md", "c.md"], messages);
    }

    [Fact]
    public async Task Send_ReturnsFalse_WhenNobodyListens()
    {
        Assert.False(await SingleInstanceChannel.SendAsync(UniqueName(), "x.md", TimeSpan.FromMilliseconds(300)));
    }
}
