using System.Collections.Concurrent;

namespace FourDev.Mail;

// Wolverine handlers wait until the World actor processes the notification. No background handler touches WorldState.
public sealed class MailNotificationInbox
{
    private readonly ConcurrentQueue<(MailDelivered Mail, TaskCompletionSource Done)> _pending = new();
    public async Task DeliverAsync(MailDelivered mail, CancellationToken cancellation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue((mail, done));
        try { await done.Task.WaitAsync(cancellation); }
        catch (OperationCanceledException) { done.TrySetCanceled(cancellation); throw; }
    }
    public void Drain(Action<MailDelivered> deliver)
    {
        while (_pending.TryDequeue(out var next))
        {
            if (next.Done.Task.IsCompleted) continue;
            try { deliver(next.Mail); next.Done.TrySetResult(); }
            catch (Exception ex) { next.Done.TrySetException(ex); }
        }
    }
}

public static class MailNotificationHandler
{
    public static Task Handle(MailDelivered mail, MailNotificationInbox inbox, CancellationToken cancellation)
        => inbox.DeliverAsync(mail, cancellation);
}
