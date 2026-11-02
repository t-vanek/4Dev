using FourDev.Mail;
using Xunit;

namespace TMap.Server.Tests;

public sealed class MailNotificationInboxTests
{
    private static MailDelivered Mail(uint id = 1) => new(id, 2, "Operator", "Hero", "Title", 1, 100);
    [Fact]
    public async Task BackgroundDeliveryCompletesOnlyAfterTheActorDrainsItInOrder()
    {
        var inbox = new MailNotificationInbox(); var a = inbox.DeliverAsync(Mail(1), default); var b = inbox.DeliverAsync(Mail(2), default);
        Assert.False(a.IsCompleted); Assert.False(b.IsCompleted);
        var seen = new List<uint>(); inbox.Drain(m => seen.Add(m.PostId)); await Task.WhenAll(a, b);
        Assert.Equal(new uint[] { 1, 2 }, seen);
    }
    [Fact]
    public async Task CanceledHandlerCannotDeliverALateActorCallback()
    {
        var inbox = new MailNotificationInbox(); using var stop = new CancellationTokenSource();
        var waiting = inbox.DeliverAsync(Mail(), stop.Token); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        inbox.Drain(_ => throw new InvalidOperationException("Canceled notification ran"));
    }
    [Fact]
    public async Task ActorFailureReachesWolverineInsteadOfAcknowledgingSuccess()
    {
        var inbox = new MailNotificationInbox(); var waiting = inbox.DeliverAsync(Mail(), default);
        inbox.Drain(_ => throw new IOException("actor unavailable"));
        await Assert.ThrowsAsync<IOException>(() => waiting);
    }
}
