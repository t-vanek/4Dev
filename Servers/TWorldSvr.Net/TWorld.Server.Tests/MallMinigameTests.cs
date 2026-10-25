using TWorld.Protocol;
using TWorld.Server.World;
using Xunit;

namespace TWorld.Server.Tests;

/// <summary>Final MW slice — guild economy (point-reward, monster-buy), cash-mall, minigames (RPS, meeting
/// room), battle-mode status, and summon creation. All single-map, DB-free; "route to main"/"fan to
/// connections" relays land back on the same client.</summary>
public class MallMinigameTests
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
    public async Task GuildPointReward_Chief_SpendsUseablePoints_AndReplies()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 80, 0x80);

        var guild = new Guild { Id = 9, Name = "Knights", Chief = 80, PvPUseablePoint = 5000, PvPTotalPoint = 10000 };
        guild.Members[80] = new GuildMember { CharId = 80, Name = "Chief", Duty = (byte)GuildDuty.Chief };
        guild.Members[81] = new GuildMember { CharId = 81, Name = "Target" };   // offline → passes the save check
        host.State.Guilds[9] = guild;
        host.State.Characters[80].Guild = guild;

        var p = new PacketWriter(Msg.MW_GUILDPOINTREWARD_ACK);
        p.WriteUInt32(80); p.WriteUInt32(0x80); p.WriteString("Target"); p.WriteUInt32(1000); p.WriteString("gg");
        client.Send(p);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_GUILDPOINTREWARD_REQ, req.Id);
        Assert.Equal((byte)GprResult.Success, req.ReadByte());
        Assert.Equal(80u, req.ReadUInt32());
        Assert.Equal(0x80u, req.ReadUInt32());
        Assert.Equal(4000u, req.ReadUInt32());   // remaining useable
        Assert.Equal(1000u, req.ReadUInt32());   // point
        Assert.Equal(81u, req.ReadUInt32());      // targetId
        Assert.Equal("Target", req.ReadString());
        Assert.Equal("gg", req.ReadString());

        Assert.Equal(4000u, guild.PvPUseablePoint);
        Assert.Single(guild.PointRewards);
    }

    [Fact]
    public async Task GuildPointReward_NotEnoughPoints_Refused()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 88, 0x88);

        var guild = new Guild { Id = 11, Chief = 88, PvPUseablePoint = 100 };
        guild.Members[88] = new GuildMember { CharId = 88, Name = "Chief", Duty = (byte)GuildDuty.Chief };
        guild.Members[89] = new GuildMember { CharId = 89, Name = "Mate" };
        host.State.Guilds[11] = guild;
        host.State.Characters[88].Guild = guild;

        var p = new PacketWriter(Msg.MW_GUILDPOINTREWARD_ACK);
        p.WriteUInt32(88); p.WriteUInt32(0x88); p.WriteString("Mate"); p.WriteUInt32(1000); p.WriteString("");
        client.Send(p);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_GUILDPOINTREWARD_REQ, req.Id);
        Assert.Equal((byte)GprResult.NeedPoint, req.ReadByte());
        Assert.Equal(100u, guild.PvPUseablePoint);   // unchanged
    }

    [Fact]
    public async Task MonsterBuy_SpendsTreasury_OnSuccess()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 82, 0x82);

        var guild = new Guild { Id = 10, Gold = 1 };   // 1 gold = 1,000,000 copper
        host.State.Guilds[10] = guild;
        host.State.Characters[82].Guild = guild;

        var b = new PacketWriter(Msg.MW_MONSTERBUY_ACK);
        b.WriteUInt32(82); b.WriteUInt32(0x82); b.WriteUInt16(5); b.WriteUInt16(7); b.WriteUInt32(500_000);
        client.Send(b);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_MONSTERBUY_REQ, req.Id);
        Assert.Equal((byte)MonsterBuyResult.Success, req.ReadByte());
        Assert.Equal(82u, req.ReadUInt32());
        Assert.Equal(0x82u, req.ReadUInt32());
        Assert.Equal(10u, req.ReadUInt32());          // guildId
        Assert.Equal((ushort)5, req.ReadUInt16());     // npcId
        Assert.Equal((ushort)7, req.ReadUInt16());     // id
        Assert.Equal(500_000u, req.ReadUInt32());      // price

        Assert.Equal(0u, guild.Gold);
        Assert.Equal(500u, guild.Silver);              // 500,000 copper left
        Assert.Equal(0u, guild.Cooper);
    }

    [Fact]
    public async Task MonsterBuy_NeedMoney_WhenTreasuryShort()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 90, 0x90);

        var guild = new Guild { Id = 12, Cooper = 100 };
        host.State.Guilds[12] = guild;
        host.State.Characters[90].Guild = guild;

        var b = new PacketWriter(Msg.MW_MONSTERBUY_ACK);
        b.WriteUInt32(90); b.WriteUInt32(0x90); b.WriteUInt16(1); b.WriteUInt16(1); b.WriteUInt32(5000);
        client.Send(b);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_MONSTERBUY_REQ, req.Id);
        Assert.Equal((byte)MonsterBuyResult.NeedMoney, req.ReadByte());
        Assert.Equal(100u, guild.Cooper);   // unchanged
    }

    [Fact]
    public async Task BattleModeStatus_NoBattles_RepliesWithZeroedBlocks()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 83, 0x83);

        var q = new PacketWriter(Msg.MW_BATTLEMODESTATUS_REQ);
        q.WriteUInt32(83); q.WriteUInt32(0x83);
        client.Send(q);

        var ack = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_BATTLEMODESTATUS_ACK, ack.Id);
        Assert.Equal(83u, ack.ReadUInt32());
        Assert.Equal(0x83u, ack.ReadUInt32());
        // BoW block (no system → winner defaults to TCONTRY_N)
        Assert.Equal((byte)0, ack.ReadByte());
        Assert.Equal(0u, ack.ReadUInt32());
        Assert.Equal((byte)Contry.None, ack.ReadByte());
        // BR block
        Assert.Equal((byte)0, ack.ReadByte());
        Assert.Equal(0u, ack.ReadUInt32());
        Assert.Equal((byte)0, ack.ReadByte());
    }

    [Fact]
    public async Task RpsGame_WinKeep_DeniesOncePeriodIsFull()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 84, 0x84);

        var rps = new RpsGame { Type = 1, WinCount = 3, WinKeep = 2, WinPeriod = 7 };
        host.State.RpsGames[(ushort)(1 | (3 << 8))] = rps;

        // First two plays stand, the third is denied (concurrent == WinKeep).
        for (int i = 0; i < 3; i++)
        {
            var play = new PacketWriter(Msg.MW_RPSGAME_ACK);
            play.WriteUInt32(84); play.WriteUInt32(0x84); play.WriteByte(1); play.WriteByte(3); play.WriteByte(2);
            client.Send(play);

            var req = client.Receive(TimeSpan.FromSeconds(5));
            Assert.Equal(Msg.MW_RPSGAME_REQ, req.Id);
            Assert.Equal(84u, req.ReadUInt32());
            Assert.Equal(0x84u, req.ReadUInt32());
            bool result = req.ReadBool();
            Assert.Equal((byte)2, req.ReadByte());   // playerRps echoed
            Assert.Equal(i < 2, result);
        }
        Assert.Equal(2, rps.WinDates.Count);
    }

    [Fact]
    public async Task RpsGame_UnconfiguredChart_Denied()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 91, 0x91);

        var play = new PacketWriter(Msg.MW_RPSGAME_ACK);
        play.WriteUInt32(91); play.WriteUInt32(0x91); play.WriteByte(9); play.WriteByte(9); play.WriteByte(1);
        client.Send(play);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_RPSGAME_REQ, req.Id);
        req.ReadUInt32(); req.ReadUInt32();
        Assert.False(req.ReadBool());
    }

    [Fact]
    public async Task MeetingRoom_Invite_ForwardsToTargetMap()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 85, 0x85);   // inviter
        EnterChar(client, 86, 0x86);   // target

        var inviter = host.State.Characters[85]; inviter.Name = "Inviter"; host.State.CharactersByName["Inviter"] = inviter;
        inviter.MapId = (ushort)(Proto.MeetingMapId + 1);
        var target = host.State.Characters[86];
        target.Name = "Target"; host.State.CharactersByName["Target"] = target;
        target.PosX = 3 * Proto.UnitSize; target.PosZ = 0; target.MapId = 0;   // sitting at the invite spot, not yet in a room

        var m = new PacketWriter(Msg.MW_MEETINGROOM_ACK);
        m.WriteUInt32(85); m.WriteUInt32(0x85); m.WriteByte(0); m.WriteByte(0); m.WriteString("Target");
        client.Send(m);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_MEETINGROOM_REQ, req.Id);
        Assert.Equal(86u, req.ReadUInt32());            // routed to the target
        Assert.Equal(0x86u, req.ReadUInt32());
        Assert.Equal((byte)0, req.ReadByte());          // type (invite)
        Assert.Equal((byte)MeetingResult.Success, req.ReadByte());
        Assert.Equal("Inviter", req.ReadString());      // inviter's name
    }

    [Fact]
    public async Task MeetingRoom_Accept_TeleportsInvitedGuestIntoRoom()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 92, 0x92);   // room owner (already inside a small room)
        EnterChar(client, 93, 0x93);   // joiner — sends the accept and gets teleported in

        var owner = host.State.Characters[92]; owner.Name = "Owner"; host.State.CharactersByName["Owner"] = owner;
        owner.MapId = (ushort)(Proto.MeetingMapId + 1); owner.Channel = 2;
        owner.PosX = 10; owner.PosY = 20; owner.PosZ = 30;
        var joiner = host.State.Characters[93]; joiner.Name = "Joiner"; host.State.CharactersByName["Joiner"] = joiner;
        joiner.MapId = 0; joiner.PosX = 3 * Proto.UnitSize; joiner.PosZ = 0;

        client.Send(new PacketWriter(Msg.MW_MEETINGROOM_ACK).WriteUInt32(92).WriteUInt32(0x92)
            .WriteByte(0).WriteByte(0).WriteString("Joiner"));
        Assert.Equal(Msg.MW_MEETINGROOM_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);

        // The joiner (93) confirms; pChar=joiner is teleported to pTarget=Owner's room.
        var m = new PacketWriter(Msg.MW_MEETINGROOM_ACK);
        m.WriteUInt32(93); m.WriteUInt32(0x93); m.WriteByte(1); m.WriteByte((byte)MeetingResult.Success); m.WriteString("Owner");
        client.Send(m);

        var move = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.CT_USERMOVE_ACK, move.Id);
        Assert.Equal(93u, move.ReadUInt32());            // the joiner gets teleported
        Assert.Equal(0x93u, move.ReadUInt32());
        Assert.Equal((byte)2, move.ReadByte());           // owner's channel
        Assert.Equal((ushort)(Proto.MeetingMapId + 1), move.ReadUInt16());
        Assert.Equal(10f, move.ReadFloat());
        Assert.Equal(20f, move.ReadFloat());
        Assert.Equal(30f, move.ReadFloat());
        Assert.Equal((ushort)0, move.ReadUInt16());       // wPartyID default

        // An accepted invitation is single-use, even while both characters remain online.
        client.Send(m);
        var replay = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_MEETINGROOM_REQ, replay.Id); replay.ReadUInt32(); replay.ReadUInt32(); replay.ReadByte();
        Assert.Equal((byte)MeetingResult.NoTarget, replay.ReadByte());
    }

    [Fact]
    public async Task MeetingRoom_AcceptWithoutInvitation_DoesNotTeleport()
    {
        await using var host = new WorldTestHost(); using var client = await host.ConnectAsync(); Connect(client);
        EnterChar(client, 92, 0x92); EnterChar(client, 93, 0x93);
        var owner = host.State.Characters[92]; owner.Name = "Owner"; owner.MapId = (ushort)(Proto.MeetingMapId + 1);
        host.State.CharactersByName["Owner"] = owner;
        client.Send(new PacketWriter(Msg.MW_MEETINGROOM_ACK).WriteUInt32(93).WriteUInt32(0x93)
            .WriteByte(1).WriteByte(0).WriteString("Owner"));
        var denied = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_MEETINGROOM_REQ, denied.Id); denied.ReadUInt32(); denied.ReadUInt32(); denied.ReadByte();
        Assert.Equal((byte)MeetingResult.NoTarget, denied.ReadByte());
    }

    [Fact]
    public async Task CreateRecallMon_AllocatesId_AndForwardsRecord()
    {
        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();
        Connect(client);
        EnterChar(client, 87, 0x87);

        var a = new PacketWriter(Msg.MW_CREATERECALLMON_ACK);
        a.WriteUInt32(87); a.WriteUInt32(0x87); a.WriteUInt32(0);   // monId 0 → server allocates
        a.WriteUInt16(1234);            // wMon
        a.WriteUInt32(0xABCD);          // dwATTR
        a.WriteUInt16(55);              // wPetID
        a.WriteByte(1);                 // bEffect
        a.WriteString("Fluffy");        // strName
        a.WriteByte(20);                // bLevel
        a.WriteByte(2); a.WriteByte(3); a.WriteByte(0); a.WriteByte(0); a.WriteByte(0); // class/race/action/status/mode
        a.WriteUInt32(1000); a.WriteUInt32(500); a.WriteUInt32(900); a.WriteUInt32(450); // maxhp/maxmp/hp/mp
        a.WriteByte(7);                 // bHit
        a.WriteByte(5);                 // bSkillLevel
        a.WriteFloat(1f); a.WriteFloat(2f); a.WriteFloat(3f); // pos
        a.WriteUInt16(180);             // wDir
        a.WriteUInt32(60);              // dwTime
        a.WriteByte(1);                 // bRecallAuto
        a.WriteUInt32(0);               // dwTargetID
        a.WriteByte(0);                 // bTargetType
        a.WriteByte(1);                 // bSkillCount
        a.WriteUInt16(4001);            // skill[0]
        client.Send(a);

        var req = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_CREATERECALLMON_REQ, req.Id);
        Assert.Equal(87u, req.ReadUInt32());
        Assert.Equal(0x87u, req.ReadUInt32());
        Assert.Equal(1u, req.ReadUInt32());        // allocated monId
        Assert.Equal((ushort)1234, req.ReadUInt16());
        Assert.Equal(0xABCDu, req.ReadUInt32());
        Assert.Equal((ushort)55, req.ReadUInt16());
        Assert.Equal((byte)1, req.ReadByte());
        Assert.Equal("Fluffy", req.ReadString());
        Assert.Equal(1u, host.State.GenRecallId);
    }
}
