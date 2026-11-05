using ADHDWindows.Core.Events;

namespace ADHDWindows.Tests;

public class EventBusTests
{
    private readonly RecordingLog _log = new();

    [Fact]
    public void Publish_delivers_event_only_to_subscribers_of_its_type()
    {
        var bus = new EventBus(_log);
        var received = new List<string>();
        var numbers = new List<int>();
        bus.Subscribe<string>(received.Add);
        bus.Subscribe<int>(numbers.Add);

        bus.Publish("событие");

        Assert.Equal(["событие"], received);
        Assert.Empty(numbers);
    }

    [Fact]
    public void Disposed_subscription_receives_nothing()
    {
        var bus = new EventBus(_log);
        var count = 0;
        var subscription = bus.Subscribe<string>(_ => count++);

        subscription.Dispose();
        subscription.Dispose();
        bus.Publish("событие");

        Assert.Equal(0, count);
    }

    [Fact]
    public void Failing_subscriber_is_logged_and_does_not_block_others()
    {
        var bus = new EventBus(_log);
        var delivered = false;
        bus.Subscribe<string>(_ => throw new InvalidOperationException());
        bus.Subscribe<string>(_ => delivered = true);

        bus.Publish("событие");

        Assert.True(delivered);
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Subscriber_can_unsubscribe_itself_during_delivery()
    {
        var bus = new EventBus(_log);
        var count = 0;
        IDisposable? subscription = null;
        subscription = bus.Subscribe<string>(_ =>
        {
            count++;
            subscription!.Dispose();
        });

        bus.Publish("первое");
        bus.Publish("второе");

        Assert.Equal(1, count);
    }
}
