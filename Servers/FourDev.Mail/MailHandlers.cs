using TMap.Data;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace FourDev.Mail;

public sealed class MailSqlBackend(string gameConnection, string? globalConnection)
{
    public GameDatabase Map { get; } = new(gameConnection, globalConnection);
    public TWorld.Data.GameDatabase World { get; } = new(gameConnection);
}

// These handlers touch SQL only. Interactive operations are invoked and awaited by the owning game actor.
public static class MailHandlers
{
    public static async Task<MailReply<(int, uint)>> Handle(CheckMailRecipient c, MailSqlBackend db)
        => new(await db.Map.PostCanSendAsync(c.Sender, c.Target, c.Type));
    public static async Task<MailReply<(int, uint, uint)>> Handle(SaveMailHeader c, MailSqlBackend db)
        => new(await db.Map.SavePostAsync(c.SenderId, c.ReceiverId, c.Target, c.Sender, c.Title, c.Message, c.Read, c.Type, c.Gold, c.Silver, c.Cooper, c.Time));
    public static async Task<MailReply<bool>> Handle(SaveMailAttachment c, MailSqlBackend db)
    { await db.Map.SavePostItemAsync(c.ReceiverId, c.Item); return new(true); }
    public static async Task<MailReply<(ushort, ushort, uint)>> Handle(ReadMailCounts c, MailSqlBackend db)
        => new(await db.Map.PostInfoAsync(c.CharId, c.Page));
    public static async Task<MailReply<List<PostListRow>>> Handle(ReadMailPage c, MailSqlBackend db)
        => new(await db.Map.PostListAsync(c.CharId, c.BeginId));
    public static async Task<MailReply<PostViewData>> Handle(ReadMail c, MailSqlBackend db)
        => new(await db.Map.PostViewAsync(c.CharId, c.PostId));
    public static async Task<MailReply<List<FullItemRow>>> Handle(ReadMailAttachments c, MailSqlBackend db)
        => new(await db.Map.PostItemsAsync(c.CharId, c.PostId));
    public static async Task<MailReply<int>> Handle(DeleteMail c, MailSqlBackend db)
        => new(await db.Map.PostDeleteAsync(c.CharId, c.PostId));
    public static async Task<MailReply<bool>> Handle(ReleaseMailContents c, MailSqlBackend db)
    { await db.Map.PostGetItemAsync(c.CharId, c.PostId); return new(true); }
    public static async Task<MailReply<PostBillResult>> Handle(SettleMailBill c, MailSqlBackend db)
        => new(await db.Map.PostBillsUpdateAsync(c.PostId, c.Name, c.Type));
    public static async Task<MailReply<List<(uint, long)>>> Handle(ReadPendingMailBills c, MailSqlBackend db)
        => new(await db.Map.PendingBillsAsync(c.CharId));
    public static async Task<MailReply<QuestPostResult>> Handle(ReserveQuestMail c, MailSqlBackend db)
        => new(await db.Map.ReserveQuestPostAsync(c.QuestId, c.CharId, c.Items, c.CountMax));
    public static async Task<MailReply<QuestPostResult>> Handle(ReserveQuestMailOperation c, MailSqlBackend db)
        => new(await db.Map.ReserveQuestPostAsync(c.Intent));
    public static async Task<MailReply<IReadOnlyList<QuestPostOperation>>> Handle(ReadQuestMailOperations c, MailSqlBackend db)
        => new(await db.Map.LoadQuestPostOperationsAsync(c.CharId));
    public static async Task<MailReply<bool>> Handle(AcknowledgeQuestMailOperations c, MailSqlBackend db)
    { await db.Map.AcknowledgeQuestPostOperationsAsync(c.CharId, c.Operations); return new(true); }
    public static async Task<MailReply<bool>> Handle(VerifyMailStorage c, MailSqlBackend db)
    { await db.Map.VerifyOperatorPostSchemaAsync(); return new(true); }
    public static async Task<MailReply<TWorld.Data.ReservedPostResult>> Handle(PullReservedMail c, MailSqlBackend db)
        => new(await db.World.DeliverReservedPostAsync());

    public static async Task Handle(DeliverServerMail c, MailSqlBackend db, MessageContext context, IWolverineRuntime runtime)
        => await Deliver(c.Intent, db, context, runtime);

    public static async Task<MailReply<OperatorPostResult>> Handle(DeliverServerMailNow c, MailSqlBackend db, MessageContext context, IWolverineRuntime runtime)
        => new(await Deliver(c.Intent, db, context, runtime));

    private static async Task<OperatorPostResult> Deliver(OperatorPostIntent intent, MailSqlBackend db, MessageContext context, IWolverineRuntime runtime)
    {
        // Persist the notification envelope inside the SAME SQL transaction as native mail/items/receipt.
        // The old permanent receipt remains the business deduplication boundary after inbox retention expires.
        return await db.Map.DeliverOperatorPostAsync(intent, async (_, tx, result) =>
        {
            await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction((IMessageDatabase)runtime.Storage, tx));
            await context.SendAsync(new MailDelivered(result.PostId, result.CharId, "Operator", result.Receiver,
                intent.Title, intent.Type, intent.Time));
        });
    }
}
