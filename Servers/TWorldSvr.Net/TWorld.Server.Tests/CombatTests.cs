using TWorld.Protocol;
using TWorld.Server.World;
using Xunit;

namespace TWorld.Server.Tests;

/// <summary>Phase 5b — combat/progression/loot. On a single map server the "route to main" relays come back
/// to the same connection (main == sender), so we can assert the re-emitted _REQ on the same client.</summary>
public class CombatTests
{
    private const byte ServerId = 1, ServerType = 4;
    private static ushort WId => (ushort)((ServerType << 8) | ServerId);

    private static void Connect(TcpTestClient client)
    {
        var c = new PacketWriter(Msg.MW_CONNECT_ACK);
        c.WriteUInt16(WId); c.WriteByte(1); c.WriteByte(0);
        client.Send(c);
    }

    private static void EnterChar(TcpTestClient client, uint charId, uint key)
    {
        var add = new PacketWriter(Msg.MW_ADDCHAR_ACK);
        add.WriteUInt32(charId); add.WriteUInt32(key); add.WriteUInt32(0x0100007F); add.WriteUInt16(5816); add.WriteUInt32(charId + 1000);
        client.Send(add);
        Assert.Equal(Msg.MW_ENTERSVR_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);

        var cd = new PacketWriter(Msg.MW_CHARDATA_ACK);
        cd.WriteUInt32(charId); cd.WriteUInt32(key); cd.WriteByte(0); cd.WriteByte(20);
        cd.WriteUInt32(500); cd.WriteUInt32(500); cd.WriteUInt32(200); cd.WriteUInt32(200);
        cd.WriteByte(0); cd.WriteByte(0); cd.WriteByte(0);
        client.Send(cd);
        Assert.Equal(Msg.MW_ENTERCHAR_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);

        var ack = new PacketWriter(Msg.MW_ENTERCHAR_ACK);
        ack.WriteUInt32(charId); ack.WriteUInt32(key);
        client.Send(ack);

        Assert.Equal(Msg.MW_CHECKMAIN_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);
        var cm = new PacketWriter(Msg.MW_CHECKMAIN_ACK);
        cm.WriteUInt32(charId); cm.WriteUInt32(key);
        client.Send(cm);
        Assert.Equal(Msg.MW_CONRESULT_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);
    }

    [Fact]
    public async Task LevelUp_UpdatesStoredLevel()
    {
        const uint charId = 20, key = 0xAA01;
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, charId, key);

        var lu = new PacketWriter(Msg.MW_LEVELUP_ACK);
        lu.WriteUInt32(charId); lu.WriteUInt32(key); lu.WriteByte(41);
        client.Send(lu);

        await Task.Delay(80);
        Assert.Equal((byte)41, host.State.Characters[charId].Level);
    }

    [Fact]
    public async Task MonsterDie_FullRewardAndHuntContractIsPreserved()
    {
        await using var host = new WorldTestHost();using var client = await host.ConnectAsync();
        Connect(client);EnterChar(client, 22, 0xAA03);
        using var main = await host.ConnectAsync();
        main.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x0402).WriteByte(1).WriteByte(1));
        for (int i=0;i<200 && !host.State.Servers.ContainsKey(0x0402);i++) await Task.Delay(5);
        Assert.True(host.State.Servers.ContainsKey(0x0402));
        host.State.Characters[22].MainId=2;
        host.State.Characters[22].Connections[2]=new CharConnection { ServerId=2,Ready=true };
        client.Send(new PacketWriter(Msg.MW_MONSTERDIE_ACK).WriteUInt32(22).WriteUInt32(0xAA03).WriteUInt32(322)
            .WriteUInt32(0x70001).WriteFloat(100).WriteFloat(7).WriteFloat(102).WriteUInt16(600).WriteByte(3).WriteByte(5).WriteByte(1));
        var r = main.Receive(TimeSpan.FromSeconds(5));Assert.Equal(Msg.MW_MONSTERDIE_REQ,r.Id);
        Assert.Equal((22u,0xAA03u,322u,0x70001u),(r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32()));
        Assert.Equal((100f,7f,102f),(r.ReadFloat(),r.ReadFloat(),r.ReadFloat()));
        Assert.Equal(((ushort)600,(byte)3,(byte)5,(byte)1),(r.ReadUInt16(),r.ReadByte(),r.ReadByte(),r.ReadByte()));
        Assert.Equal(0,r.Remaining);
    }

    [Fact]
    public async Task MonsterDie_ForwardedToMainVerbatim()
    {
        const uint charId = 21, key = 0xAA02;
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, charId, key);

        var md = new PacketWriter(Msg.MW_MONSTERDIE_ACK);
        md.WriteUInt32(charId); md.WriteUInt32(key); md.WriteUInt32(0xDEAD); md.WriteUInt16(7);
        client.Send(md);

        // Single server: main == sender, so the forwarded _REQ comes straight back with the body intact.
        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_MONSTERDIE_REQ, req.Id);
        Assert.Equal(charId, req.ReadUInt32());
        Assert.Equal(key, req.ReadUInt32());
        Assert.Equal(0xDEADu, req.ReadUInt32());
        Assert.Equal((ushort)7, req.ReadUInt16());
    }

    [Fact]
    public async Task AddItem_UnknownChar_RepliesNotFound()
    {
        const uint charId = 22, key = 0xAA03;
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, charId, key);

        // Wrong key -> char not found -> ADDITEMRESULT NOTFOUND back to the reporting server.
        var ai = new PacketWriter(Msg.MW_ADDITEM_ACK);
        ai.WriteUInt32(charId); ai.WriteUInt32(0xBADBAD);      // bad key
        ai.WriteByte(ServerId); ai.WriteByte(0); ai.WriteUInt16(3); ai.WriteUInt32(0xC0DE);
        ai.WriteByte(0); ai.WriteByte(0); ai.WriteByte(55);    // inven, slot, itemId
        client.Send(ai);

        var res = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_ADDITEMRESULT_REQ, res.Id);
        Assert.Equal(charId, res.ReadUInt32());
        Assert.Equal(0xBADBADu, res.ReadUInt32());
        res.ReadByte();                      // channel
        Assert.Equal((ushort)3, res.ReadUInt16());
        Assert.Equal(0xC0DEu, res.ReadUInt32());
        Assert.Equal((byte)55, res.ReadByte());
        Assert.Equal((byte)MonItemTake.NotFound, res.ReadByte());
    }

    [Fact]
    public async Task AddItem_ValidChar_ForwardedToMain()
    {
        const uint charId = 23, key = 0xAA04;
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, charId, key);

        var ai = new PacketWriter(Msg.MW_ADDITEM_ACK);
        ai.WriteUInt32(charId); ai.WriteUInt32(key);
        ai.WriteByte(ServerId); ai.WriteByte(0); ai.WriteUInt16(3); ai.WriteUInt32(0xC0DE);
        ai.WriteByte(1); ai.WriteByte(2); ai.WriteByte(55);
        client.Send(ai);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_ADDITEM_REQ, req.Id);
        Assert.Equal(charId, req.ReadUInt32());
        Assert.Equal(key, req.ReadUInt32());
    }

    [Fact]
    public async Task PartyOrderTakeItem_RoutesToNextMember_CarriesItemVerbatim()
    {
        const uint idA = 24, keyA = 0xAA05, idB = 25, keyB = 0xAA06;
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, idA, keyA);
        EnterChar(client, idB, keyB);

        // White-box: form an ordered-loot party of the two entered chars (Order seeds to the first member).
        var chA = host.State.Characters[idA];
        var chB = host.State.Characters[idB];
        var party = new Party { Id = 0x300, ObtainType = 5 /* PT_ORDER */, ChiefId = idA };
        party.AddMember(chA); party.AddMember(chB);
        host.State.Parties[party.Id] = party;

        byte[] itemBlob = { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        var ot = new PacketWriter(Msg.MW_PARTYORDERTAKEITEM_ACK);
        ot.WriteUInt32(idA); ot.WriteUInt32(keyA); ot.WriteUInt16(party.Id);
        ot.WriteByte(ServerId); ot.WriteByte(0); ot.WriteUInt16(2); ot.WriteUInt32(0x999); ot.WriteUInt16(0);
        ot.WriteByte(2); ot.WriteUInt32(idA); ot.WriteUInt32(idB);   // eligible members
        ot.WriteRaw(itemBlob);
        client.Send(ot);

        // Order pointer started at member A, so the first ordered drop goes to A.
        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_PARTYORDERTAKEITEM_REQ, req.Id);
        Assert.Equal(idA, req.ReadUInt32());
        Assert.Equal(keyA, req.ReadUInt32());
        Assert.Equal(ServerId, req.ReadByte());
        req.ReadByte();                          // channel
        Assert.Equal((ushort)2, req.ReadUInt16());
        Assert.Equal(0x999u, req.ReadUInt32());
        Assert.Equal((ushort)0, req.ReadUInt16());
        Assert.Equal(itemBlob, req.ReadRemaining().ToArray());

        // Rotation advanced to B.
        Assert.Equal(idB, party.Order);
    }
}
