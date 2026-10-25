using System.Net.Sockets;
using TWorld.Server.Security;

namespace TWorld.Server.Tests;

// Explicit test-only transport for existing gameplay fixtures. Production never installs it.
internal sealed class TestPeerAuthenticator : IPeerAuthenticator
{
    public Task<AuthenticatedPeer> AuthenticateAsync(Socket socket, CancellationToken token) =>
        Task.FromResult(new AuthenticatedPeer(new NetworkStream(socket, ownsSocket: false), new(PeerRole.Map, 0)));
}
