using TMap.Data;
using Wolverine;
using Microsoft.Extensions.DependencyInjection;

namespace FourDev.Mail;

public sealed class WolverineMailStore(IServiceScopeFactory scopes) : IPostStore, IQuestPostOperationStore, IOperatorPostStore, TWorld.Data.IReservedPostStore
{
    private async Task<T> Call<T>(object message)
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<MailReply<T>>(message, timeout: TimeSpan.FromSeconds(60))).Value;
    }
    public Task<(int Result, uint RecvId)> PostCanSendAsync(string sender, string target, byte type)
        => Call<(int, uint)>(new CheckMailRecipient(sender, target, type));
    public Task<(int Result, uint PostId, uint RecvId)> SavePostAsync(uint sendId, uint recvId, string target, string sender,
        string title, string message, byte read, byte type, uint gold, uint silver, uint cooper, long timeRecv)
        => Call<(int, uint, uint)>(new SaveMailHeader(sendId, recvId, target, sender, title, message, read, type, gold, silver, cooper, timeRecv));
    public async Task SavePostItemAsync(uint recvId, ItemSaveData item) => await Call<bool>(new SaveMailAttachment(recvId, item));
    public Task<(ushort Total, ushort NotRead, uint BeginId)> PostInfoAsync(uint charId, ushort page)
        => Call<(ushort, ushort, uint)>(new ReadMailCounts(charId, page));
    public Task<List<PostListRow>> PostListAsync(uint charId, uint beginId) => Call<List<PostListRow>>(new ReadMailPage(charId, beginId));
    public Task<PostViewData> PostViewAsync(uint charId, uint postId) => Call<PostViewData>(new ReadMail(charId, postId));
    public Task<List<FullItemRow>> PostItemsAsync(uint charId, uint postId) => Call<List<FullItemRow>>(new ReadMailAttachments(charId, postId));
    public Task<int> PostDeleteAsync(uint charId, uint postId) => Call<int>(new DeleteMail(charId, postId));
    public async Task PostGetItemAsync(uint charId, uint postId) => await Call<bool>(new ReleaseMailContents(charId, postId));
    public Task<PostBillResult> PostBillsUpdateAsync(uint postId, string charName, byte type) => Call<PostBillResult>(new SettleMailBill(postId, charName, type));
    public Task<List<(uint PostId, long TimeRecv)>> PendingBillsAsync(uint sendId) => Call<List<(uint, long)>>(new ReadPendingMailBills(sendId));
    public Task<QuestPostResult> ReserveQuestPostAsync(uint questId, uint charId, IReadOnlyList<ItemSaveData> items, byte countMax = 0)
        => Call<QuestPostResult>(new ReserveQuestMail(questId, charId, items.ToList(), countMax));
    public Task<QuestPostResult> ReserveQuestPostAsync(QuestPostIntent intent) => Call<QuestPostResult>(new ReserveQuestMailOperation(intent));
    public Task<IReadOnlyList<QuestPostOperation>> LoadQuestPostOperationsAsync(uint charId) => Call<IReadOnlyList<QuestPostOperation>>(new ReadQuestMailOperations(charId));
    public async Task AcknowledgeQuestPostOperationsAsync(uint charId, IReadOnlyList<Guid> operations)
        => await Call<bool>(new AcknowledgeQuestMailOperations(charId, operations.ToList()));
    public async Task VerifyOperatorPostSchemaAsync() => await Call<bool>(new VerifyMailStorage());
    // Receipt replay remains available for callers that require the result synchronously.
    public Task<OperatorPostResult> DeliverOperatorPostAsync(OperatorPostIntent intent)
        => Call<OperatorPostResult>(new DeliverServerMailNow(intent));
    public Task<TWorld.Data.ReservedPostResult> DeliverReservedPostAsync() => Call<TWorld.Data.ReservedPostResult>(new PullReservedMail());

    public void Enqueue(OperatorPostIntent intent)
    {
        intent.Validate();
        // A synchronous durability boundary is intentional: existing gameplay callers can ignore the returned Task.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMessageBus>().SendAsync(new DeliverServerMail(intent)).AsTask().GetAwaiter().GetResult();
    }
    public Task ForwardLegacyIntentAsync(OperatorPostIntent intent)
    { Enqueue(intent); return Task.CompletedTask; }
}
