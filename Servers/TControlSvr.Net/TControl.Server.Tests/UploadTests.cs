using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TControl.Protocol;
using TControl.Server.Ops;
using Xunit;

namespace TControl.Server.Tests;

public class UploadTests
{
    private static LocalFileDeploy Deploy(TestDirectory directory, long limit = 1024)
        => new(Options.Create(new ControlServerOptions { UploadRoot = directory.Path, MaxUploadBytes = limit }),
            new TestEnvironment(directory.Path), NullLogger<LocalFileDeploy>.Instance);

    [Fact]
    public void CommitIsAtomic_CancelPreservesPreviousFile_AndLimitIsEnforced()
    {
        using var directory = new TestDirectory();
        using var deploy = Deploy(directory, limit: 4);
        string target = Path.Combine(directory.Path, "server.bin");
        File.WriteAllText(target, "previous");
        Assert.Equal(0, deploy.Begin(0, "server.bin"));
        Assert.Equal(0, deploy.Write(new byte[] { 1, 2, 3 }));
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal(4, deploy.Write(new byte[] { 4, 5 }));
        Assert.Equal(0, deploy.End(cancel: true));
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal(0, deploy.Begin(0, "server.bin"));
        Assert.Equal(0, deploy.Write(new byte[] { 8, 9 }));
        Assert.Equal(0, deploy.End(cancel: false));
        Assert.Equal(new byte[] { 8, 9 }, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("../escape.bin")] [InlineData("/escape.bin")] [InlineData("C:/escape.bin")]
    [InlineData("sub/../../escape.bin")] [InlineData("file:stream")] [InlineData("sub\\escape.bin")]
    [InlineData("")] [InlineData("sub/../escape.bin")]
    public void UnsafeUploadNamesAreRejected(string name)
    {
        using var directory = new TestDirectory();
        using var deploy = Deploy(directory);
        Assert.NotEqual(0, deploy.Begin(0, name));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void DisabledOrRemoteDeploymentIsRejected()
    {
        using var directory = new TestDirectory();
        using var deploy = Deploy(directory);
        Assert.NotEqual(0, deploy.Begin(1, "server.bin"));
        using var disabled = new LocalFileDeploy(Options.Create(new ControlServerOptions()),
            new TestEnvironment(directory.Path), NullLogger<LocalFileDeploy>.Instance);
        Assert.NotEqual(0, disabled.Begin(0, "server.bin"));
    }

    [Fact]
    public async Task WireUpload_IncludesFinalChunk_AndAnotherSessionCannotCancelIt()
    {
        using var directory = new TestDirectory();
        using var deploy = Deploy(directory);
        await using var host = new ControlHarness(files: deploy);
        using var owner = await host.ConnectAsync();
        using var other = await host.ConnectAsync();
        ControlHarness.Login(owner);
        ControlHarness.Login(other);
        owner.Send(new PacketWriter(Msg.CT_SERVICEUPLOADSTART_REQ).WriteByte(0).WriteString("server.bin"));
        Assert.Equal(0, new PacketReader(owner.ReceiveUntil(Msg.CT_SERVICEUPLOADSTART_ACK, ControlHarness.Timeout)).ReadByte());
        other.Send(new PacketWriter(Msg.CT_SERVICEUPLOADEND_REQ).WriteByte(1));
        Assert.Equal(4, new PacketReader(other.ReceiveUntil(Msg.CT_SERVICEUPLOADEND_ACK, ControlHarness.Timeout)).ReadByte());
        owner.Send(new PacketWriter(Msg.CT_SERVICEUPLOAD_REQ).WriteUInt16(2).WriteRaw(new byte[] { 1, 2 }));
        owner.ReceiveUntil(Msg.CT_SERVICEUPLOAD_ACK, ControlHarness.Timeout);
        owner.Send(new PacketWriter(Msg.CT_SERVICEUPLOADEND_REQ).WriteByte(0).WriteUInt16(2).WriteRaw(new byte[] { 3, 4 }));
        Assert.Equal(0, new PacketReader(owner.ReceiveUntil(Msg.CT_SERVICEUPLOADEND_ACK, ControlHarness.Timeout)).ReadByte());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(directory.Path, "server.bin")));
    }

    [Fact]
    public async Task TruncatedWireChunk_AbortsAndPreservesPreviousFile()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "server.bin"), "previous");
        using var deploy = Deploy(directory);
        await using var host = new ControlHarness(files: deploy);
        using var client = await host.ConnectAsync();
        ControlHarness.Login(client);
        client.Send(new PacketWriter(Msg.CT_SERVICEUPLOADSTART_REQ).WriteByte(0).WriteString("server.bin"));
        client.ReceiveUntil(Msg.CT_SERVICEUPLOADSTART_ACK, ControlHarness.Timeout);
        client.Send(new PacketWriter(Msg.CT_SERVICEUPLOAD_REQ).WriteUInt16(10).WriteByte(1));
        Assert.Equal(4, new PacketReader(client.ReceiveUntil(Msg.CT_SERVICEUPLOADEND_ACK, ControlHarness.Timeout)).ReadByte());
        Assert.Equal("previous", File.ReadAllText(Path.Combine(directory.Path, "server.bin")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
