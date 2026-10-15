using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// PtyOutputRelay — what a PTY host prints before its caller subscribes. The macOS runner's
/// PTY test lost an echoed marker on every push because the read thread raised it first.
/// </summary>
public sealed class PtyOutputRelayTests
{
    [Fact]
    public void Output_before_the_first_subscriber_is_replayed_to_it_in_order()
    {
        var relay = new PtyOutputRelay();
        relay.Raise("hello ");
        relay.Raise("world");

        var seen = new List<string>();
        relay.Subscribe(seen.Add);
        relay.Raise("!");

        Assert.Equal(new[] { "hello world", "!" }, seen);
    }

    [Fact]
    public void Only_the_first_subscriber_gets_the_replay()
    {
        var relay = new PtyOutputRelay();
        relay.Raise("early");

        var first = new List<string>();
        var second = new List<string>();
        relay.Subscribe(first.Add);
        relay.Subscribe(second.Add);
        relay.Raise("late");

        Assert.Equal(new[] { "early", "late" }, first);
        Assert.Equal(new[] { "late" }, second);
    }

    [Fact]
    public void Early_output_is_capped_and_an_unsubscribed_handler_hears_nothing()
    {
        var relay = new PtyOutputRelay();
        relay.Raise(new string('x', PtyOutputRelay.EarlyCapChars + 500));

        var seen = new List<string>();
        Action<string> handler = seen.Add;
        relay.Subscribe(handler);
        Assert.Equal(PtyOutputRelay.EarlyCapChars, seen.Single().Length);

        relay.Unsubscribe(handler);
        relay.Raise("after");
        Assert.Single(seen);
    }

    [Fact]
    public void Concurrent_raise_during_subscribe_keeps_the_writer_order()
    {
        var relay = new PtyOutputRelay();
        var writer = Task.Run(() => { for (var i = 0; i < 2000; i++) relay.Raise(i + ","); });
        Thread.Sleep(1);
        var sb = new System.Text.StringBuilder();
        relay.Subscribe(s => sb.Append(s));
        writer.Wait();

        var numbers = sb.ToString().TrimEnd(',').Split(',').Select(int.Parse).ToList();
        Assert.Equal(Enumerable.Range(0, 2000), numbers);
    }
}
