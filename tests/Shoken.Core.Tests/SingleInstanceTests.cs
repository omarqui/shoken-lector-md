using Shoken.Core.Ipc;

namespace Shoken.Core.Tests;

public class SingleInstanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string UniqueName() => "ShokenPrueba-" + Guid.NewGuid().ToString("N");

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
        channel.AttachHandler(message => received.TrySetResult(message));

        Assert.True(await SingleInstanceChannel.SendAsync(name, @"C:\Mis docs\guía.md", Timeout));
        Assert.Equal(@"C:\Mis docs\guía.md", await received.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Send_EmptyMeansShow()
    {
        string name = UniqueName();
        using SingleInstanceChannel channel = SingleInstanceChannel.TryClaim(name)!;
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.AttachHandler(message => received.TrySetResult(message));

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
        channel.AttachHandler(message =>
        {
            lock (messages)
            {
                messages.Add(message);
                if (messages.Count == 3)
                {
                    done.TrySetResult();
                }
            }
        });

        foreach (string path in new[] { "a.md", "b.md", "c.md" })
        {
            Assert.True(await SingleInstanceChannel.SendAsync(name, path, Timeout));
        }

        await done.Task.WaitAsync(Timeout);
        Assert.Equal(["a.md", "b.md", "c.md"], messages);
    }

    [Fact]
    public async Task MessagesThatArriveBeforeTheWindowIsReady_AreKeptAndDeliveredInOrder()
    {
        // Abrir 3 .md juntos desde el Explorador: las otras instancias escriben mientras la primera arranca.
        string name = UniqueName();
        using SingleInstanceChannel channel = SingleInstanceChannel.TryClaim(name)!;
        Assert.True(await SingleInstanceChannel.SendAsync(name, "a.md", Timeout));
        Assert.True(await SingleInstanceChannel.SendAsync(name, "b.md", Timeout));
        await Task.Delay(200);

        var messages = new List<string?>();
        channel.AttachHandler(message =>
        {
            lock (messages)
            {
                messages.Add(message);
            }
        });

        Assert.Equal(["a.md", "b.md"], messages);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        SingleInstanceChannel channel = SingleInstanceChannel.TryClaim(UniqueName())!;

        channel.Dispose();
        channel.Dispose();
    }

    [Fact]
    public async Task Send_ReturnsFalse_WhenNobodyListens()
    {
        Assert.False(await SingleInstanceChannel.SendAsync(UniqueName(), "x.md", TimeSpan.FromMilliseconds(300)));
    }
}
