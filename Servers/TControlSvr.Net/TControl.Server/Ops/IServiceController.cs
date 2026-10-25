using TControl.Server.Control;

namespace TControl.Server.Ops;

/// <summary>Lifecycle operations for configured local server processes.</summary>
public interface IServiceController
{
    bool Start(ServiceInstance svc);
    Task<bool> StopAsync(ServiceInstance svc, CancellationToken cancellationToken = default);
    /// <summary>Process state, or null for a peer not managed by this controller.</summary>
    uint? GetStatus(ServiceInstance svc);
    bool? GetRestartIntent(ServiceInstance svc) => null;
    void SetRestartIntent(ServiceInstance svc, bool enabled) { }
}
