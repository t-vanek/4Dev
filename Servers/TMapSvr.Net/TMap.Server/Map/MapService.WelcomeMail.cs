using Microsoft.Extensions.Logging;
using TMap.Data;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    public IWelcomeMailStore? WelcomeMailStore { get; set; }
    private readonly HashSet<ClientSession> _welcomeMailRetries = new();

    private OperatorPostIntent BuildWelcomeMail(WelcomeMailSettings settings, WelcomeMailIdentity identity)
    {
        if (settings.Title.Length > 248 || settings.Message.Length > 2040 || settings.Items.Count == 0)
            throw new InvalidDataException("Welcome mail requires a title/message within protocol limits and a gift.");
        var rows = new List<ItemSaveData>();
        foreach (var gift in settings.Items)
        {
            if (!_templates.Items.TryGetValue(gift.ItemId, out var template) || gift.Count is < 1 or > 65280 || template.Stack == 0)
                throw new InvalidDataException($"Welcome gift {gift.ItemId} is not a usable item template/count.");
            int remaining = gift.Count;
            while (remaining > 0)
            {
                if (rows.Count == byte.MaxValue) throw new InvalidDataException("Welcome gift exceeds the mail attachment limit.");
                var item = new Item { TemplateId = gift.ItemId, Template = template, Count = (byte)Math.Min(remaining, template.Stack),
                    DuraMax = template.DuraMax, DuraCur = template.DuraMax };
                LinkItemAttr(item);
                rows.Add(BuildItemSave(0, item) with { DlId = 0, StorageType = 2, StorageId = 0, ItemSlot = (byte)rows.Count });
                remaining -= item.Count;
            }
        }
        var intent = new OperatorPostIntent(identity.OperationId, identity.CharId, identity.Receiver,
            NetString(settings.Title), NetString(settings.Message), 1, 0, 0, 0, identity.Time, rows);
        intent.Validate(); return intent;
    }

    public async Task VerifyWelcomeMailStorageAsync()
    {
        if (WelcomeMailStore is not { } store) return;
        var settings = await store.VerifyWelcomeMailStorageAsync();
        if (settings.Enabled) BuildWelcomeMail(settings, new(1, Guid.NewGuid(), "Validation", UnixNow()));
    }

    private async Task DispatchWelcomeMailAsync(OperatorPostIntent intent)
    {
        if (OperatorPostEnqueue is { } enqueue) enqueue(intent); // returns only after Wolverine SQL transport accepts it
        else if (OperatorPostStore is { } posts)
        {
            var result = await posts.DeliverOperatorPostAsync(intent);
            if (result.Result is not (0 or 1)) throw new IOException("Welcome mail delivery was not durably accepted or rejected.");
            if (result.Created) NotifyPostRecv(result.PostId, "Operator", result.Receiver, intent.Title, intent.Type, intent.Time);
        }
        else throw new InvalidOperationException("Welcome mail requires durable SQL mail delivery.");
        await WelcomeMailStore!.MarkWelcomeMailDispatchedAsync(intent.OperationId);
    }

    private async Task WelcomeMailAtEntryAsync(ClientSession s)
    {
        if (WelcomeMailStore is not { } store || s.WelcomeMailChecked || !s.IsMain || s.State != EnterState.InGame
            || s.TeleportPending || s.Grid is null || s.Char is not { DbLoaded: true, TournamentBackup: null }
            || !ReferenceEquals(_state.FindByChar(s.CharId), s)) return;
        try
        {
            var intent = await store.PrepareWelcomeMailAsync(s.CharId, BuildWelcomeMail);
            if (intent is not null) await DispatchWelcomeMailAsync(intent);
            s.WelcomeMailChecked = true; _welcomeMailRetries.Remove(s);
        }
        catch (Exception ex)
        {
            _welcomeMailRetries.Add(s);
            _log.LogWarning(ex, "Welcome mail for character {Character} remains pending in SQL/retry.", s.CharId);
        }
    }

    public async Task RecoverWelcomeMailAsync()
    {
        if (WelcomeMailStore is not { } store) return;
        while (true)
        {
            var pending = await store.PendingWelcomeMailAsync();
            if (pending.Count == 0) return;
            foreach (var intent in pending) await DispatchWelcomeMailAsync(intent);
        }
    }

    private async Task RetryWelcomeMailAsync()
    {
        if (WelcomeMailStore is null || _tickSeconds % 30 != 0) return;
        foreach (var s in _welcomeMailRetries.ToArray())
        {
            if (!ReferenceEquals(_state.FindByChar(s.CharId), s) || s.State != EnterState.InGame) _welcomeMailRetries.Remove(s);
            else await WelcomeMailAtEntryAsync(s);
        }
        try { await RecoverWelcomeMailAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Pending welcome mail remains in SQL for retry."); }
    }
}
