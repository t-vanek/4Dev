using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>An in-memory mailbox with the stored procedures' contracts (TPostCanSend / TSavePost / TPostView /
/// TPostDelete / TPostGetItem / TPostBillsUpdate, as read from the live DB).</summary>
internal sealed class FakePostStore : IPostStore
{
    public sealed class Mail
    {
        public uint Id, SendId, CharId; public string Sender = "", Target = "", Title = "", Message = "";
        public byte Type, Read; public uint Gold, Silver, Cooper; public long Time;
        public List<ItemSaveData> Items = new();
    }

    public readonly Dictionary<string, uint> Chars = new();
    public readonly List<Mail> Mails = new();
    public int FailSave;
    public bool FailRelease;
    private uint _next = 100;

    public Task<(int, uint)> PostCanSendAsync(string sender, string target, byte type)
        => Task.FromResult(Chars.TryGetValue(target, out var id)
            ? (type < 3 && Mails.Count(m => m.CharId == id) >= 20 ? 6 : 0, id)
            : (1, 0u));

    public Task<(int, uint, uint)> SavePostAsync(uint sendId, uint recvId, string target, string sender, string title,
        string message, byte read, byte type, uint gold, uint silver, uint cooper, long timeRecv)
    {
        if (FailSave != 0) return Task.FromResult((FailSave, 0u, 0u));
        var m = new Mail { Id = ++_next, SendId = sendId, CharId = recvId, Target = target, Sender = sender, Title = title,
            Message = message, Read = read, Type = type, Gold = gold, Silver = silver, Cooper = cooper, Time = timeRecv };
        Mails.Add(m);
        return Task.FromResult((0, m.Id, recvId));
    }

    public Task SavePostItemAsync(uint recvId, ItemSaveData item)
    {
        Mails.Single(m => m.Id == item.StorageId).Items.Add(item);
        return Task.CompletedTask;
    }

    public Task<(ushort, ushort, uint)> PostInfoAsync(uint charId, ushort page)
    {
        var mine = Mails.Where(m => m.CharId == charId).ToList();
        return Task.FromResult(((ushort)mine.Count, (ushort)mine.Count(m => m.Read == 0), mine.Count == 0 ? 0u : mine.Max(m => m.Id)));
    }

    public Task<List<PostListRow>> PostListAsync(uint charId, uint beginId)
        => Task.FromResult(Mails.Where(m => m.CharId == charId && m.Id <= beginId).OrderByDescending(m => m.Id)
            .Select(m => new PostListRow(m.Id, m.Read, m.Type, m.Sender, m.Title, m.Time, (byte)(m.Items.Count > 0 ? 1 : 0))).ToList());

    public Task<PostViewData> PostViewAsync(uint charId, uint postId)
    {
        var m = Mails.FirstOrDefault(x => x.CharId == charId && x.Id == postId);
        return Task.FromResult(m is null
            ? new PostViewData(1, 0, 0, 0, 0, 0, 0, 0, 0, "", "", "", 0)
            : new PostViewData(0, (byte)m.Items.Count, m.SendId, m.Type, m.Read, m.Gold, m.Silver, m.Cooper, m.Time,
                m.Sender, m.Title, m.Message, (byte)(m.Items.Count > 0 ? 1 : 0)));
    }

    public Task<List<FullItemRow>> PostItemsAsync(uint charId, uint postId)
        => Task.FromResult(Mails.Single(m => m.Id == postId).Items.Select(i => new FullItemRow(StoragePost, postId,
            i.ItemSlot, i.TemplateId, i.Level, i.Count, i.GLevel, i.DuraMax, i.DuraCur, i.RefineCur, i.EndTime,
            i.GradeEffect, i.Magic, i.Value, i.Ext, i.Gem, i.MoggItemId, i.DlId)).ToList());

    private const byte StoragePost = 2;

    public Task<int> PostDeleteAsync(uint charId, uint postId)
    {
        var m = Mails.FirstOrDefault(x => x.CharId == charId && x.Id == postId);
        if (m is null) return Task.FromResult(0);
        if (m.Gold + m.Silver + m.Cooper > 0) return Task.FromResult(1);
        if (m.Items.Count > 0) return Task.FromResult(2);
        Mails.Remove(m);
        return Task.FromResult(0);
    }

    public Task PostGetItemAsync(uint charId, uint postId)
    {
        if (FailRelease) return Task.FromException(new IOException("injected mail release failure"));
        var m = Mails.Single(x => x.CharId == charId && x.Id == postId);
        m.Gold = m.Silver = m.Cooper = 0;
        m.Items.Clear();
        return Task.CompletedTask;
    }

    /// <summary>TPostBillsUpdate as the live proc does it: a return (3) turns the bill itself round to its sender —
    /// same id, items re-owned; a payment (4) mails the money to the sender and leaves the items in the bill for
    /// the payer to take. The name must match the receiver, unless empty (the system's expiry).</summary>
    public Task<PostBillResult> PostBillsUpdateAsync(uint postId, string charName, byte type)
    {
        var m = Mails.FirstOrDefault(x => x.Id == postId && x.Read == 0);
        if (m is null || (charName != "" && m.Target != charName)) return Task.FromResult(new PostBillResult(8, 0, "", "", ""));
        string sender = m.Sender, recver = m.Target;
        if (type == 3)
        {
            (m.CharId, m.SendId) = (m.SendId, m.CharId);
            (m.Sender, m.Target) = (recver, sender);
            m.Type = 3; m.Gold = m.Silver = m.Cooper = 0;
            return Task.FromResult(new PostBillResult(0, m.Id, recver, sender, m.Title));
        }
        var paid = new Mail { Id = ++_next, CharId = m.SendId, SendId = m.CharId, Sender = recver, Target = sender,
            Title = m.Title, Type = type, Gold = m.Gold, Silver = m.Silver, Cooper = m.Cooper, Time = m.Time };
        Mails.Add(paid);
        m.Gold = m.Silver = m.Cooper = 0; m.Read = 1;
        return Task.FromResult(new PostBillResult(0, paid.Id, recver, sender, m.Title));
    }

    public Task<List<(uint, long)>> PendingBillsAsync(uint sendId)
        => Task.FromResult(Mails.Where(m => m.SendId == sendId && m.Type == 2 && m.Read == 0).Select(m => (m.Id, m.Time)).ToList());
}

/// <summary>Mail, the map's half: C++ OnCS_POST*_REQ and their DM_POST* database steps.</summary>
public class PostTests
{
    private const ushort Sword = 7001;

    private static async Task<(MapTestHarness h, FakePostStore db, ClientSession s1, FakeClientChannel c1, Character a,
        ClientSession s2, FakeClientChannel c2, Character b)> Setup()
    {
        var t = new TemplateStore();
        t.Items[Sword] = new ItemTemplate(Sword, 0, new float[4], Type: 1, IsSell: 5 /* ITEMTRADE_DEAL | ITEMTRADE_CABINET */);
        var h = new MapTestHarness(t);
        var db = new FakePostStore();
        h.Service.PostStore = db;
        h.Service.UnixNow = () => 1_000_000;
        var a = new Character { CharId = 1, Name = "Alice", MaxHp = 100, Hp = 100 };
        var bag = new Inven { InvenId = 0 };
        bag.Items.Add(new Item { ItemSlot = 3, TemplateId = Sword, Template = t.Items[Sword], Count = 1 });
        a.Invens.Add(bag);
        var (s1, c1) = await h.EnterAsync(1, 1, 1, name: "Alice", preSeeded: a);
        var b = new Character { CharId = 2, Name = "Bob", MaxHp = 100, Hp = 100 };
        b.Invens.Add(new Inven { InvenId = 0 });
        var (s2, c2) = await h.EnterAsync(2, 2, 2, name: "Bob", preSeeded: b);
        a.Name = "Alice"; b.Name = "Bob";
        a.Gold = 0; a.Silver = 5; a.Cooper = 0;                       // 5000 copper
        db.Chars["Alice"] = 1; db.Chars["Bob"] = 2;
        c1.Clear(); c2.Clear();
        return (h, db, s1, c1, a, s2, c2, b);
    }

    private static byte[] Send(string to, string title, byte type, uint cooper = 0, byte inven = 0xFC, byte slot = 0xFF)
    {
        var w = new PacketWriter(Msg.CS_POSTSEND_REQ);
        w.WriteString(to); w.WriteString(title); w.WriteString("hello"); w.WriteByte(type);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(cooper); w.WriteByte(inven); w.WriteByte(slot);
        return w.ToArray();
    }

    private static byte[] Id(ushort msg, uint postId, params byte[] extra)
    {
        var w = new PacketWriter(msg);
        w.WriteUInt32(postId);
        foreach (var b in extra) w.WriteByte(b);
        return w.ToArray();
    }

    private static byte Result(FakeClientChannel c, ushort ack) => new PacketReader(c.Last(ack)!).ReadByte();

    // ================= send =================

    [Fact]
    public async Task ALetter_CostsPostage_AndTellsTheReceiver()
    {
        var (h, db, s1, c1, a, _, c2, _) = await Setup();

        await h.Service.DispatchClientAsync(s1, Send("Bob", "Hi", 0 /* POST_NORMAL */));

        Assert.Equal(0, Result(c1, Msg.CS_POSTSEND_ACK));
        Assert.Equal(5000 - 100, a.MoneyTotal);
        var mail = Assert.Single(db.Mails);
        Assert.Equal(2u, mail.CharId);
        var r = new PacketReader(c2.Last(Msg.CS_POSTRECV_ACK)!);
        Assert.Equal(mail.Id, r.ReadUInt32());
        r.ReadByte(); r.ReadByte();
        Assert.Equal("Alice", r.ReadString());
        Assert.Equal("Hi", r.ReadString());
    }

    [Fact]
    public async Task APackage_MovesTheItemIntoTheMail()
    {
        var (h, db, s1, c1, a, _, _, _) = await Setup();

        await h.Service.DispatchClientAsync(s1, Send("Bob", "Gift", 1 /* POST_PACKATE */, inven: 0, slot: 3));

        Assert.Equal(0, Result(c1, Msg.CS_POSTSEND_ACK));
        Assert.Empty(a.Invens[0].Items);
        Assert.True(c1.Has(Msg.CS_DELITEM_ACK));
        var item = Assert.Single(db.Mails.Single().Items);
        Assert.Equal(Sword, item.TemplateId);
        Assert.Equal(2, item.StorageType);                            // STORAGE_POST
        Assert.Equal(5000 - 300, a.MoneyTotal);
    }

    [Theory]
    [InlineData("Nobody", "Hi", 1)]    // POST_NORECEIVER
    [InlineData("Alice", "Hi", 14)]    // POST_SAMEACCOUNT
    [InlineData("Bob", "", 5)]         // POST_NOTITLE
    public async Task TheSendGates(string to, string title, byte expected)
    {
        var (h, db, s1, c1, a, _, _, _) = await Setup();

        await h.Service.DispatchClientAsync(s1, Send(to, title, 0));

        Assert.Equal(expected, Result(c1, Msg.CS_POSTSEND_ACK));
        Assert.Empty(db.Mails);
        Assert.Equal(5000, a.MoneyTotal);
    }

    [Fact]
    public async Task NotEnoughForThePostage_Refuses()
    {
        var (h, db, s1, c1, a, _, _, _) = await Setup();
        a.Silver = 0; a.Cooper = 50;

        await h.Service.DispatchClientAsync(s1, Send("Bob", "Hi", 0));

        Assert.Equal(3, Result(c1, Msg.CS_POSTSEND_ACK));            // POST_NEEDMONEY
        Assert.Empty(db.Mails);
    }

    [Fact]
    public async Task ABillWithoutAnItem_Refuses()
    {
        var (h, db, s1, c1, _, _, _, _) = await Setup();

        await h.Service.DispatchClientAsync(s1, Send("Bob", "Pay me", 2 /* POST_BILLS */, cooper: 500));

        Assert.Equal(4, Result(c1, Msg.CS_POSTSEND_ACK));            // POST_NEEDITEM
    }

    [Fact]
    public async Task AFailedSave_KeepsTheItemAndTheMoney()
    {
        var (h, db, s1, c1, a, _, _, _) = await Setup();
        db.FailSave = 11;                                              // POST_INTERNAL

        await h.Service.DispatchClientAsync(s1, Send("Bob", "Gift", 1, inven: 0, slot: 3));

        Assert.Equal(11, Result(c1, Msg.CS_POSTSEND_ACK));
        Assert.Single(a.Invens[0].Items);
        Assert.Equal(5000, a.MoneyTotal);
    }

    // ================= read, take, delete =================

    [Fact]
    public async Task FailedMailReleaseCannotGrantContentsAndCanRetryWithoutDuplicateMoney()
    {
        var (h, db, sender, _, _, receiver, client, ch) = await Setup();
        await h.Service.DispatchClientAsync(sender, Send("Bob", "Gift", 1, cooper: 700, inven: 0, slot: 3));
        uint id = Assert.Single(db.Mails).Id;
        await h.Service.DispatchClientAsync(receiver, Id(Msg.CS_POSTVIEW_REQ, id));
        db.FailRelease = true;
        await h.Service.DispatchClientAsync(receiver, Id(Msg.CS_POSTGETITEM_REQ, id));
        Assert.Equal(11, Result(client, Msg.CS_POSTGETITEM_ACK)); Assert.Equal(0, ch.MoneyTotal);
        Assert.Empty(ch.Invens[0].Items); Assert.Single(ch.OpenPost!.Items); Assert.Single(db.Mails[0].Items);
        db.FailRelease = false;
        await h.Service.DispatchClientAsync(receiver, Id(Msg.CS_POSTGETITEM_REQ, id));
        Assert.Equal(0, Result(client, Msg.CS_POSTGETITEM_ACK)); Assert.Equal(700, ch.MoneyTotal);
        Assert.Single(ch.Invens[0].Items); Assert.Empty(db.Mails[0].Items);
        await h.Service.DispatchClientAsync(receiver, Id(Msg.CS_POSTGETITEM_REQ, id)); Assert.Equal(700, ch.MoneyTotal);
    }

    [Fact]
    public async Task ReadingAndTakingAPackage_GivesTheItemAndMoney()
    {
        var (h, db, s1, _, _, s2, c2, b) = await Setup();
        await h.Service.DispatchClientAsync(s1, Send("Bob", "Gift", 1, cooper: 700, inven: 0, slot: 3));
        uint id = db.Mails.Single().Id;
        c2.Clear();

        var list = new PacketWriter(Msg.CS_POSTLIST_REQ);
        list.WriteUInt16(0);
        await h.Service.DispatchClientAsync(s2, list.ToArray());
        var lr = new PacketReader(c2.Last(Msg.CS_POSTLIST_ACK)!);
        Assert.Equal(1, lr.ReadUInt16());                              // total
        lr.ReadUInt16(); lr.ReadUInt16();
        Assert.Equal(1, lr.ReadUInt16());                              // rows
        Assert.Equal(id, lr.ReadUInt32());

        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTVIEW_REQ, id));
        Assert.NotNull(b.OpenPost);
        Assert.Single(b.OpenPost!.Items);

        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTGETITEM_REQ, id));
        Assert.Equal(0, Result(c2, Msg.CS_POSTGETITEM_ACK));
        Assert.Contains(b.Invens[0].Items, i => i.TemplateId == Sword);
        Assert.Equal(700, b.MoneyTotal);
        Assert.Empty(db.Mails.Single().Items);                         // released in the store
    }

    [Fact]
    public async Task TakingFromAMailThatIsNotOpen_IsNotFound()
    {
        var (h, _, _, _, _, s2, c2, _) = await Setup();

        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTGETITEM_REQ, 999));

        Assert.Equal(8, Result(c2, Msg.CS_POSTGETITEM_ACK));          // POST_NOTFOUND
    }

    [Fact]
    public async Task AMailStillHoldingMoney_CannotBeDeleted()
    {
        var (h, db, s1, _, _, s2, c2, _) = await Setup();
        await h.Service.DispatchClientAsync(s1, Send("Bob", "Cash", 0, cooper: 700));
        uint id = db.Mails.Single().Id;

        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTDEL_REQ, id));

        Assert.Equal(0u, new PacketReader(c2.Last(Msg.CS_POSTDEL_ACK)!).ReadUInt32());   // refused ⇒ id 0
        Assert.Single(db.Mails);
    }

    // ================= bills =================

    private static async Task<uint> SendBill(MapTestHarness h, FakePostStore db, ClientSession s1)
    {
        await h.Service.DispatchClientAsync(s1, Send("Bob", "Pay me", 2, cooper: 800, inven: 0, slot: 3));
        return db.Mails.Single(m => m.Type == 2).Id;
    }

    [Fact]
    public async Task PayingABill_SendsTheMoneyToItsSender()
    {
        var (h, db, s1, c1, _, s2, c2, b) = await Setup();
        uint id = await SendBill(h, db, s1);
        b.Cooper = 1000;
        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTVIEW_REQ, id));

        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTRETURN_REQ, id, 1 /* pay */));

        Assert.Equal(200, b.MoneyTotal);                               // 1000 − 800
        Assert.Equal(0, Result(c2, Msg.CS_POSTRETURN_ACK));
        var paid = db.Mails.Single(m => m.Type == 4);                  // POST_PAYMENT to Alice
        Assert.Equal(1u, paid.CharId);
        Assert.Equal(800u, paid.Cooper);
        Assert.True(c1.Has(Msg.CS_POSTRECV_ACK));                      // Alice is told
        Assert.Single(db.Mails.Single(m => m.Id == id).Items);         // the item waits in the bill for Bob
    }

    [Fact]
    public async Task ReturningABill_SendsTheItemBack()
    {
        var (h, db, s1, _, _, s2, c2, _) = await Setup();
        uint id = await SendBill(h, db, s1);
        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTVIEW_REQ, id));

        await h.Service.DispatchClientAsync(s2, Id(Msg.CS_POSTRETURN_REQ, id, 0 /* return */));

        Assert.Equal(id, new PacketReader(c2.Last(Msg.CS_POSTDEL_ACK)!).ReadUInt32());
        var back = db.Mails.Single(m => m.Id == id);                   // the same mail, turned round
        Assert.Equal(3, back.Type);                                    // POST_RETURN
        Assert.Equal(1u, back.CharId);                                 // now Alice's
        Assert.Single(back.Items);
    }

    [Fact]
    public async Task AnUnpaidBill_GoesBackAfterThreeDays_WhileItsSenderIsOnline()
    {
        var (h, db, s1, _, _, _, _, _) = await Setup();
        await SendBill(h, db, s1);

        h.Service.UnixNow = () => 1_000_000 + 86400 * 3 - 1;
        await h.Service.RunPostBills();
        Assert.DoesNotContain(db.Mails, m => m.Type == 3);             // not yet

        h.Service.UnixNow = () => 1_000_000 + 86400 * 3;
        await h.Service.RunPostBills();
        Assert.Contains(db.Mails, m => m.Type == 3 && m.CharId == 1);  // returned to Alice
    }

    [Fact]
    public async Task TheSweep_StopsAtTheFirstBillNotYetDue()
    {
        // The C++ walks bills in post-id order and breaks at the first one not due — so a later bill that is
        // already due waits behind an earlier one that is not (TMapSvr.cpp:6256). Ported as is.
        var (h, db, s1, _, a, _, _, _) = await Setup();
        a.Invens[0].Items.Add(new Item { ItemSlot = 4, TemplateId = Sword, Template = a.Invens[0].Items[0].Template, Count = 1 });
        await h.Service.DispatchClientAsync(s1, Send("Bob", "Late", 2, cooper: 10, inven: 0, slot: 3));    // lower id, due later
        h.Service.UnixNow = () => 900_000;
        await h.Service.DispatchClientAsync(s1, Send("Bob", "Early", 2, cooper: 10, inven: 0, slot: 4));   // higher id, due first

        h.Service.UnixNow = () => 900_000 + 86400 * 3;
        await h.Service.RunPostBills();

        Assert.DoesNotContain(db.Mails, m => m.Type == 3);             // "Early" is due, but waits behind "Late"
    }

    [Fact]
    public async Task ABillIsForgotten_WhenItsSenderLogsOut()
    {
        var (h, db, s1, _, _, _, _, _) = await Setup();
        await SendBill(h, db, s1);
        h.Service.OnClientDisconnect(s1);

        h.Service.UnixNow = () => 1_000_000 + 86400 * 4;
        await h.Service.RunPostBills();

        Assert.DoesNotContain(db.Mails, m => m.Type == 3);             // C++ tracks bills only while the sender is on
    }
}
