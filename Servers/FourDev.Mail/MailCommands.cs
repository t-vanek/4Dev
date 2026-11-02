using TMap.Data;

namespace FourDev.Mail;

// SQL-facing messages contain immutable values only. Never send Character, ClientSession or packet readers.
public sealed record MailReply<T>(T Value);
public sealed record CheckMailRecipient(string Sender, string Target, byte Type);
public sealed record SaveMailHeader(uint SenderId, uint ReceiverId, string Target, string Sender,
    string Title, string Message, byte Read, byte Type, uint Gold, uint Silver, uint Cooper, long Time);
public sealed record SaveMailAttachment(uint ReceiverId, ItemSaveData Item);
public sealed record ReadMailCounts(uint CharId, ushort Page);
public sealed record ReadMailPage(uint CharId, uint BeginId);
public sealed record ReadMail(uint CharId, uint PostId);
public sealed record ReadMailAttachments(uint CharId, uint PostId);
public sealed record DeleteMail(uint CharId, uint PostId);
public sealed record ReleaseMailContents(uint CharId, uint PostId);
public sealed record SettleMailBill(uint PostId, string Name, byte Type);
public sealed record ReadPendingMailBills(uint CharId);
public sealed record ReserveQuestMail(uint QuestId, uint CharId, List<ItemSaveData> Items, byte CountMax);
public sealed record ReserveQuestMailOperation(QuestPostIntent Intent);
public sealed record ReadQuestMailOperations(uint CharId);
public sealed record AcknowledgeQuestMailOperations(uint CharId, List<Guid> Operations);
public sealed record VerifyMailStorage;
public sealed record DeliverServerMail(OperatorPostIntent Intent);
public sealed record DeliverServerMailNow(OperatorPostIntent Intent);
public sealed record PullReservedMail;
public sealed record MailDelivered(uint PostId, uint CharId, string Sender, string Receiver, string Title, byte Type, long Time);
