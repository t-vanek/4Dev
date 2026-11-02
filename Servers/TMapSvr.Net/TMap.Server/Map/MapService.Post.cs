using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Mail — C++ <c>OnCS_POST*_REQ</c> (CSHandler.cpp:8635-9026, 16542) and their <c>DM_POST*</c> database halves
/// (SSHandler.cpp:10512-11160, 17628-17780, 820-920). The C++ posts every step to its DB thread and resumes in a
/// <c>DM_*_ACK</c>; here each handler awaits the store in line, in the same order.
///
/// <para>Kinds: normal (100 copper), package (300, with an item), bill (900, an item the receiver pays for or
/// returns). Money or an item sent is held by the mail until taken. A bill the receiver neither pays nor returns
/// goes back to the sender after 3 days — tracked, as in the C++, only while the sender is online.</para>
///
/// <para><b>Changed, flagged:</b> a mail the database refuses to save no longer costs the sender its postage (the
/// C++ charged first and refunded only the item); the item attached to a mail gets a new row id under the post
/// instead of moving its old one, so the incremental inventory delete can never remove it; and a player can only
/// take a mail's contents after the database has released them (the C++ ordered it the other way round on its
/// single DB thread). <b>Not ported:</b> secure codes, tutorial and tournament gates, and the operator refund
/// letter's localized text (a plain English title is used).</para>
/// </summary>
public sealed partial class MapService
{
    private const byte PostNormal = 0, PostPackage = 1, PostBillsType = 2, PostReturnType = 3, PostPayment = 4;   // POST_TYPE
    private const byte PostSuccess = 0, PostNoReceiver = 1, PostNoItem = 2, PostNeedMoney = 3, PostNeedItem = 4,
        PostNoTitle = 5, PostInvenFull = 7, PostNotFound = 8, PostInternal = 11, PostNotDeal = 12, PostMaxLength = 13,
        PostSameAccount = 14;                                                                                   // POST_RESULT
    private const long PostDuration = 86400 * 3;                                                               // POST_DURATION
    private const byte InvenNull = 0xFC, ItemtradeCabinet = 4, StoragePost = 2;
    private const int MaxName = 50, MaxBoardTitle = 256, MaxBoardText = 2048;

    /// <summary>The mail store — the game database in production, a fake in tests.</summary>
    public IPostStore? PostStore { get; set; }

    /// <summary>Wall clock in seconds since 1970 (C++ <c>m_timeCurrent</c>). Replaceable in tests.</summary>
    public Func<long> UnixNow { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>C++ <c>m_mapTPostBills</c> (post id → expiry, walked in id order) and <c>m_mapCharPostBills</c>.</summary>
    private readonly SortedDictionary<uint, long> _postBills = new();
    private readonly Dictionary<uint, List<uint>> _charPostBills = new();

    private static uint PostCost(byte type) => type switch
    {
        PostNormal => 100, PostPackage => 300, PostBillsType => 900, _ => 0,
    };

    // The wire strings are Latin-1 (one char per byte), so Length is the C++ CString::GetLength byte count.
    private static int Cp949Length(string s) => s.Length;

    // ================================ send ================================

    private async Task OnCS_POSTSEND_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || PostStore is not { } db) return;
        if (s.Deal.Status != (byte)DealStatus.Ready || s.Store.IsOpen) { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostNotDeal); return; }

        string target = r.ReadString(), title = r.ReadString(), message = r.ReadString();
        byte type = r.ReadByte();
        uint gold = r.ReadUInt32(), silver = r.ReadUInt32(), cooper = r.ReadUInt32();
        byte inven = r.ReadByte(), slot = r.ReadByte();

        if (Cp949Length(target) > MaxName || Cp949Length(title) > MaxBoardTitle || Cp949Length(message) > MaxBoardText)
        { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostMaxLength); return; }
        if (ch.Name == target) { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostSameAccount); return; }
        if (title.Length == 0) { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostNoTitle); return; }

        (int ret, uint recvId) = await Safe(() => db.PostCanSendAsync(ch.Name, target, type), (PostNoReceiver, 0u));
        if (s.Char != ch || !s.IsMain) return;
        if (ret != 0) { SendPostResult(s, Msg.CS_POSTSEND_ACK, (byte)ret); return; }

        long money = PostCost(type) + (type != PostBillsType ? ch.MoneyOf(gold, silver, cooper) : 0);
        if (!ch.UseMoney(money, commit: false)) { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostNeedMoney); return; }

        Item? item = null;
        if (inven != InvenNull)
        {
            item = ch.FindInven(inven)?.Items.FirstOrDefault(i => i.ItemSlot == slot);
            if (item is null) { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostNeedItem); return; }
            if (!item.CanDeal() || item.Template is not { } t || (t.IsSell & ItemtradeCabinet) == 0)
            { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostNotDeal); return; }
        }
        if (type == PostBillsType && item is null) { SendPostResult(s, Msg.CS_POSTSEND_ACK, PostNeedItem); return; }

        long now = UnixNow();
        (int saved, uint postId, uint recv) = await Safe(() => db.SavePostAsync(ch.CharId, recvId, target, ch.Name, title,
            message, 0, type, gold, silver, cooper, now), (PostInternal, 0u, 0u));
        if (s.Char != ch) return;
        if (saved != 0 || postId == 0) { SendPostResult(s, Msg.CS_POSTSEND_ACK, (byte)(saved != 0 ? saved : PostInternal)); return; }

        if (item is not null)
        {
            var row = BuildItemSave(inven, item) with
            {
                DlId = _itemIdReady ? GenItemId() : 0, StorageType = StoragePost, StorageId = postId,
            };
            await Safe(async () => { await db.SavePostItemAsync(recv, row); return 0; }, 0);
            ch.FindInven(inven)?.Items.Remove(item);
            SendCS_DELITEM_ACK(s, inven, item);
        }
        if (money != 0)
        {
            ch.UseMoney(money, commit: true);
            SendCS_MONEY_ACK(s, ch);
        }
        SendPostResult(s, Msg.CS_POSTSEND_ACK, PostSuccess);

        if (type == PostBillsType) RegisterBill(ch.CharId, postId, now + PostDuration);
        NotifyPostRecv(postId, ch.Name, target, title, type, now);
    }

    /// <summary>C++ <c>OnMW_POSTRECV_REQ</c> — a mail for a player on this server, sent from another one.</summary>
    private void OnMW_POSTRECV_REQ(PacketReader r)
    {
        uint postId = r.ReadUInt32();
        string sender = r.ReadString(), target = r.ReadString(), title = r.ReadString();
        byte type = r.ReadByte();
        if (FindByName(target) is { IsMain: true } s) SendCS_POSTRECV_ACK(s, postId, sender, title, type, UnixNow());
    }

    /// <summary>The receiver is told here if it is on this server, else through the world (C++ DM_POSTRECV_ACK tail).</summary>
    private void NotifyPostRecv(uint postId, string sender, string target, string title, byte type, long now)
    {
        if (FindByName(target) is { IsMain: true } rs)
        {
            SendCS_POSTRECV_ACK(rs, postId, sender, title, type, now);
            return;
        }
        var w = new PacketWriter(Msg.MW_POSTRECV_ACK);
        w.WriteUInt32(postId); w.WriteString(sender); w.WriteString(target); w.WriteString(title); w.WriteByte(type);
        _world.Send(w);
    }

    // ================================ read ================================

    private async Task OnCS_POSTLIST_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || PostStore is not { } db) return;
        ushort page = r.ReadUInt16();

        var (total, notRead, begin) = await Safe(() => db.PostInfoAsync(ch.CharId, page), ((ushort)0, (ushort)0, 0u));
        var rows = await Safe(() => db.PostListAsync(ch.CharId, begin), new List<PostListRow>());
        if (s.Char != ch) return;
        ch.PostTotal = total; ch.PostNotRead = notRead;

        var w = new PacketWriter(Msg.CS_POSTLIST_ACK);
        w.WriteUInt16(total); w.WriteUInt16(notRead); w.WriteUInt16(page); w.WriteUInt16((ushort)rows.Count);
        foreach (var p in rows)
        {
            w.WriteUInt32(p.PostId); w.WriteByte(p.Read); w.WriteByte(p.Type); w.WriteString(p.Sender);
            w.WriteString(p.Title); w.WriteInt64(p.TimeRecv); w.WriteByte(p.Contain);
        }
        s.Send(w);
    }

    private async Task OnCS_POSTVIEW_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || PostStore is not { } db) return;
        uint postId = r.ReadUInt32();

        // One read in flight at a time; asking again for the mail already open just re-sends it.
        if (ch.PostPending != 0)
        {
            if (ch.OpenPost?.PostId == postId) SendCS_POSTVIEW_ACK(s, ch, postId);
            return;
        }
        ch.PostPending = postId;
        ch.OpenPost = null;

        var v = await Safe(() => db.PostViewAsync(ch.CharId, postId), null);
        var items = v is { Result: 0, ItemCount: > 0 }
            ? await Safe(() => db.PostItemsAsync(ch.CharId, postId), new List<FullItemRow>())
            : new List<FullItemRow>();
        if (s.Char != ch) return;

        if (v is null || v.Result != 0 || items.Count != v.ItemCount || ch.PostPending != postId)
        {
            ch.PostPending = 0;
            ch.OpenPost = null;
            return;
        }

        var post = new Post
        {
            PostId = postId, SendId = v.SendId, Sender = v.Sender, Title = v.Title, Message = v.Message, Type = v.Type,
            Read = v.Read, TimeRecv = v.TimeRecv, Gold = v.Gold, Silver = v.Silver, Cooper = v.Cooper, Contain = v.Contain,
        };
        foreach (var row in items) if (ItemFromRow(row) is { } it) post.Items.Add(it);
        ch.PostPending = 0;
        ch.OpenPost = post;
        SendCS_POSTVIEW_ACK(s, ch, postId);
    }

    // ================================ act on the open mail ================================

    private async Task OnCS_POSTDEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || PostStore is not { } db) return;
        uint postId = r.ReadUInt32();
        ch.PostPending = 0;
        ch.OpenPost = null;

        int ret = await Safe(() => db.PostDeleteAsync(ch.CharId, postId), -1);   // 1 money left, 2 item left
        if (s.Char != ch) return;
        SendCS_POSTDEL_ACK(s, ret == 0 ? postId : 0);
    }

    private async Task OnCS_POSTGETITEM_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || PostStore is not { } db) return;
        uint postId = r.ReadUInt32();
        if (ch.OpenPost is not { } post || post.PostId != postId) { SendPostResult(s, Msg.CS_POSTGETITEM_ACK, PostNotFound); return; }

        byte result = PostNoItem;
        if (post.Items.Count != 0 && !CanPush(ch, post.Items)) { SendPostResult(s, Msg.CS_POSTGETITEM_ACK, PostInvenFull); return; }

        var items = post.Items.ToList();
        long money = ch.MoneyOf(post.Gold, post.Silver, post.Cooper);
        // Release the mail's contents first: TPostGetItem deletes the post-storage rows these items still occupy.
        bool released = await Safe(async () => { await db.PostGetItemAsync(ch.CharId, postId); return true; }, false);
        if (s.Char != ch) return;
        if (!released) { SendPostResult(s, Msg.CS_POSTGETITEM_ACK, PostInternal); return; }
        post.Items.Clear();
        post.Gold = post.Silver = post.Cooper = 0;

        if (items.Count != 0)
        {
            var granted = items.Select(i => (i.TemplateId, i.Count)).ToList();
            PushTItem(s, items);
            result = PostSuccess;
            foreach (var (id, count) in granted)
                CheckQuest(s, 0, ch.PosX, ch.PosY, ch.PosZ, id, QttGetItem, TtGetItem, count);
        }
        if (money != 0)
        {
            ch.EarnMoney(money);
            SendCS_MONEY_ACK(s, ch);
            result = PostSuccess;
        }
        SendPostResult(s, Msg.CS_POSTGETITEM_ACK, result);
    }

    /// <summary>C++ <c>OnCS_POSTRETURN_REQ</c>: settle an unread bill — pay it (<c>bType</c> 1) or send it back.</summary>
    private async Task OnCS_POSTRETURN_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || PostStore is not { } db) return;
        uint postId = r.ReadUInt32();
        byte pay = r.ReadByte();
        if (ch.OpenPost is not { } post || post.PostId != postId || post.Read != 0) return;

        uint gold = post.Gold, silver = post.Silver, cooper = post.Cooper;
        if (pay != 0)
        {
            long money = ch.MoneyOf(gold, silver, cooper);
            if (money != 0)
            {
                if (!ch.UseMoney(money, commit: true)) { SendPostResult(s, Msg.CS_POSTRETURN_ACK, PostNeedMoney); return; }
                SendCS_MONEY_ACK(s, ch);
            }
        }
        await SettleBill(db, ch.CharId, ch.Name, postId, pay != 0 ? PostPayment : PostReturnType, gold, silver, cooper);
    }

    /// <summary>C++ <c>OnDM_POSTBILLUPDATE_ACK</c> + <c>PostReturn</c> (TMapSvr.cpp:7995): the bill becomes a new mail
    /// to whoever gets its contents; the settler's open copy is emptied.</summary>
    private async Task SettleBill(IPostStore db, uint charId, string charName, uint postId, byte type, uint gold, uint silver, uint cooper)
    {
        var res = await Safe(() => db.PostBillsUpdateAsync(postId, charName, type), new PostBillResult(PostNotFound, 0, "", "", ""));
        long now = UnixNow();

        if (res.Result != 0)
        {
            if (FindByName(charName) is { } fs) SendPostResult(fs, Msg.CS_POSTRETURN_ACK, (byte)res.Result);
            if (type == PostPayment)   // give the payment back, by mail (C++ an operator package)
                await Safe(() => db.SavePostAsync(0, charId, charName, "Operator", "Item error", "Your payment has been returned.",
                    0, PostPackage, gold, silver, cooper, now), (0, 0u, 0u));
            return;
        }

        NotifyPostRecv(res.NewId, res.Sender, res.RecvName, res.Title, type, now);
        if (FindByName(charName) is not { Char: { OpenPost: { } open } pc } ps || open.PostId != postId) return;
        open.Gold = open.Silver = open.Cooper = 0;
        open.Read = 1;
        EraseBill(pc.CharId, postId);
        if (type == PostPayment) SendPostResult(ps, Msg.CS_POSTRETURN_ACK, PostSuccess);
        else SendCS_POSTDEL_ACK(ps, postId);
    }

    // ================================ bills ================================

    private void RegisterBill(uint charId, uint postId, long expireAt)
    {
        _postBills.TryAdd(postId, expireAt);
        if (!_charPostBills.TryGetValue(charId, out var list)) _charPostBills[charId] = list = new List<uint>();
        list.Add(postId);
    }

    /// <summary>C++ <c>SM_POSTBILLERASE_REQ</c>: one bill, or (post id 0) every bill of a sender who logged out.</summary>
    private void EraseBill(uint charId, uint postId)
    {
        if (postId != 0) { _postBills.Remove(postId); return; }
        if (!_charPostBills.Remove(charId, out var list)) return;
        foreach (var id in list) _postBills.Remove(id);
    }

    /// <summary>At login a sender's unpaid bills are re-armed (C++ DM_POSTBILL_REQ from DM_ENTERMAPSVR_ACK).</summary>
    private async Task LoadPendingBills(uint charId)
    {
        if (PostStore is not { } db) return;
        foreach (var (postId, time) in await Safe(() => db.PendingBillsAsync(charId), new List<(uint, long)>()))
            RegisterBill(charId, postId, time + PostDuration);
    }

    /// <summary>The C++ bill sweep (TMapSvr.cpp:6256): in post-id order, every due bill is returned — stopping at
    /// the first one not yet due, exactly as the C++ loop does.</summary>
    public async Task RunPostBills()
    {
        if (PostStore is not { } db || _postBills.Count == 0) return;
        long now = UnixNow();
        var due = new List<uint>();
        foreach (var (postId, at) in _postBills)
        {
            if (at > now) break;
            due.Add(postId);
        }
        foreach (var postId in due)
        {
            _postBills.Remove(postId);
            await SettleBill(db, 0, "", postId, PostReturnType, 0, 0, 0);
        }
    }

    // ================================ senders ================================

    private static void SendPostResult(ClientSession s, ushort ack, byte result)
    {
        var w = new PacketWriter(ack);
        w.WriteByte(result);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_POSTRECV_ACK</c> (CSSender.cpp) — <c>dwPostID · bRead · bType · strSender · strTitle ·
    /// time</c>; the receiver's counters move with it.</summary>
    private static void SendCS_POSTRECV_ACK(ClientSession s, uint postId, string sender, string title, byte type, long time)
    {
        var w = new PacketWriter(Msg.CS_POSTRECV_ACK);
        w.WriteUInt32(postId); w.WriteByte(0); w.WriteByte(type); w.WriteString(sender); w.WriteString(title);
        w.WriteInt64(time);
        s.Send(w);
        if (s.Char is { } ch) { ch.PostTotal++; ch.PostNotRead++; }
    }

    /// <summary>C++ <c>SendCS_POSTVIEW_ACK</c> — the open mail, or an empty body when it is not the one asked for.</summary>
    private static void SendCS_POSTVIEW_ACK(ClientSession s, Character ch, uint postId)
    {
        var w = new PacketWriter(Msg.CS_POSTVIEW_ACK);
        w.WriteUInt32(postId);
        if (ch.OpenPost is not { } p || p.PostId != postId)
        {
            w.WriteByte(0); w.WriteString(""); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteByte(0); w.WriteByte(0);
        }
        else
        {
            w.WriteByte(p.Read); w.WriteString(p.Message); w.WriteUInt32(p.Gold); w.WriteUInt32(p.Silver);
            w.WriteUInt32(p.Cooper); w.WriteByte(p.Contain); w.WriteByte((byte)p.Items.Count);
            foreach (var it in p.Items) it.WrapPacketClient(w, ch.CharId, addItemId: false);
        }
        s.Send(w);
    }

    private static void SendCS_POSTDEL_ACK(ClientSession s, uint postId)
    {
        var w = new PacketWriter(Msg.CS_POSTDEL_ACK);
        w.WriteUInt32(postId);
        s.Send(w);
    }

    /// <summary>Runs a store call, logging and substituting <paramref name="fallback"/> on a database error — the
    /// C++ <c>if(!query->Call())</c> arms.</summary>
    private async Task<T> Safe<T>(Func<Task<T>> call, T fallback)
    {
        try { return await call(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Mail database call failed.");
            return fallback;
        }
    }
}
