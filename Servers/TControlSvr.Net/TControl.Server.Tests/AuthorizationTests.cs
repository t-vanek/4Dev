using TControl.Protocol;
using Xunit;

namespace TControl.Server.Tests;

public class AuthorizationTests
{
    public static IEnumerable<object[]> Commands => new ushort[]
    {
        Msg.CT_SERVICESTAT_REQ, Msg.CT_SERVICECONTROL_REQ, Msg.CT_SERVICEAUTOSTART_REQ, Msg.CT_SERVICEDATACLEAR_REQ,
        Msg.CT_RECONNECT_REQ, Msg.CT_CHATBANLIST_REQ, Msg.CT_CHATBANLISTDEL_REQ, Msg.CT_SERVICEUPLOADSTART_REQ,
        Msg.CT_SERVICEUPLOAD_REQ, Msg.CT_SERVICEUPLOADEND_REQ, Msg.CT_UPDATEPATCH_REQ, Msg.CT_PREVERSIONTABLE_REQ,
        Msg.CT_PREVERSIONUPDATE_REQ, Msg.CT_ANNOUNCEMENT_REQ, Msg.CT_USERKICKOUT_REQ, Msg.CT_USERMOVE_REQ,
        Msg.CT_USERPOSITION_REQ, Msg.CT_MONSPAWNFIND_REQ, Msg.CT_MONACTION_REQ, Msg.CT_USERPROTECTED_REQ,
        Msg.CT_CHARMSG_REQ, Msg.CT_CHATBAN_REQ, Msg.CT_ITEMFIND_REQ, Msg.CT_ITEMSTATE_REQ, Msg.CT_CASTLEINFO_REQ,
        Msg.CT_CASTLEGUILDCHG_REQ, Msg.CT_CASTLEENABLE_REQ, Msg.CT_HELPMESSAGE_REQ, Msg.CT_RPSGAMEDATA_REQ,
        Msg.CT_RPSGAMECHANGE_REQ, Msg.CT_TOURNAMENTEVENT_REQ, Msg.CT_EVENTQUARTERLIST_REQ, Msg.CT_EVENTQUARTERUPDATE_REQ,
        Msg.CT_CMGIFT_REQ, Msg.CT_CMGIFTLIST_REQ, Msg.CT_CMGIFTCHARTUPDATE_REQ, Msg.CT_EVENTCHANGE_REQ,
        Msg.CT_EVENTLIST_REQ, Msg.CT_CASHITEMLIST_REQ,
    }.Select(id => new object[] { id });

    [Theory, MemberData(nameof(Commands))]
    public async Task EveryManagerCommand_RejectsUnauthenticatedSession(ushort command)
    {
        await using var host = new ControlHarness();
        using var client = await host.ConnectAsync();
        client.Send(new PacketWriter(command));
        Assert.Equal(Msg.CT_AUTHORITY_ACK, PacketHeader.ReadId(client.Receive(ControlHarness.Timeout)));
        Assert.False(host.Service.State.AutoStart);
        Assert.Equal(0, host.Controller.Starts);
        Assert.Equal(0, host.Files.Begins);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(8)] [InlineData(256)]
    public async Task InvalidAuthorityFromAuthenticator_IsNotAccepted(int authority)
    {
        await using var host = new ControlHarness(new TestAuthenticator(authority));
        using var client = await host.ConnectAsync();
        Assert.Equal(1, ControlHarness.Login(client).ReadByte());
        Assert.Empty(host.Service.State.Managers);
    }

    [Fact]
    public async Task MissingDatabase_DoesNotGrantOperatorAccess()
    {
        await using var host = new ControlHarness(useDatabaseAuthenticator: true);
        using var client = await host.ConnectAsync();
        Assert.Equal(1, ControlHarness.Login(client).ReadByte());
    }

    [Fact]
    public async Task DatabaseFailure_DoesNotGrantOperatorAccess()
    {
        await using var host = new ControlHarness(new TestAuthenticator(fail: true));
        using var client = await host.ConnectAsync();
        Assert.Equal(1, ControlHarness.Login(client).ReadByte());
    }

    [Fact]
    public async Task StatusTool_CanReadStatusButCannotChangeIt()
    {
        await using var host = new ControlHarness();
        using var client = await host.ConnectAsync();
        Assert.Equal(0, ControlHarness.Login(client, readOnly: true).ReadByte());
        client.Send(new PacketWriter(Msg.CT_SERVICESTAT_REQ));
        Assert.Equal(Msg.CT_SERVICESTAT_ACK, PacketHeader.ReadId(client.ReceiveUntil(Msg.CT_SERVICESTAT_ACK, ControlHarness.Timeout)));
        client.Send(new PacketWriter(Msg.CT_SERVICEAUTOSTART_REQ).WriteByte(1));
        Assert.Equal(Msg.CT_AUTHORITY_ACK, PacketHeader.ReadId(client.ReceiveUntil(Msg.CT_AUTHORITY_ACK, ControlHarness.Timeout)));
        Assert.False(host.Service.State.AutoStart);
    }

    [Theory]
    [InlineData(Msg.CT_SERVICEAUTOSTART_REQ)] [InlineData(Msg.CT_MONACTION_REQ)]
    [InlineData(Msg.CT_RPSGAMECHANGE_REQ)] [InlineData(Msg.CT_HELPMESSAGE_REQ)]
    [InlineData(Msg.CT_TOURNAMENTEVENT_REQ)] [InlineData(Msg.CT_CMGIFT_REQ)]
    public async Task LowPrivilegeOperator_CannotUsePrivilegedCommands(ushort command)
    {
        await using var host = new ControlHarness(new TestAuthenticator((int)ManagerClass.GmLevel3));
        using var client = await host.ConnectAsync();
        Assert.Equal(0, ControlHarness.Login(client).ReadByte());
        client.Send(new PacketWriter(command));
        Assert.Equal(Msg.CT_AUTHORITY_ACK, PacketHeader.ReadId(client.ReceiveUntil(Msg.CT_AUTHORITY_ACK, ControlHarness.Timeout)));
    }

    [Fact]
    public async Task InternalPlatformPacket_CannotBeInjectedByManager()
    {
        await using var host = new ControlHarness();
        using var client = await host.ConnectAsync();
        ControlHarness.Login(client);
        client.Send(new PacketWriter(Msg.CT_PLATFORM_REQ).WriteByte(0).WriteUInt32(99).WriteUInt32(99).WriteFloat(99));
        Assert.Equal(Msg.CT_AUTHORITY_ACK, PacketHeader.ReadId(client.ReceiveUntil(Msg.CT_AUTHORITY_ACK, ControlHarness.Timeout)));
    }
}
