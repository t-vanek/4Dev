using System.Collections.Concurrent;
using TControl.Protocol;

namespace TControl.Server.Control;

public sealed record ProcessStopResult(uint ServiceId, Guid OperationId, bool Success);

public sealed partial class ControlService
{
    private sealed record PendingStop(Guid Id, ServiceInstance Service, ManagerSession Manager);
    private readonly Dictionary<uint, PendingStop> _pendingStops = new();
    private readonly Dictionary<uint, Task> _stopTasks = new();
    private readonly ConcurrentQueue<ProcessStopResult> _stopResults = new();

    private void BeginStop(ServiceInstance service, ManagerSession manager)
    {
        var pending = new PendingStop(Guid.NewGuid(), service, manager);
        _pendingStops.Add(service.Id, pending);
        Task<bool> operation;
        try { operation = _svcCtl.StopAsync(service, _operationCancellation.Token); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not begin stop for {Name}.", service.Name);
            CompleteStop(new ProcessStopResult(service.Id, pending.Id, false));
            return;
        }
        if (operation.IsCompletedSuccessfully)
            CompleteStop(new ProcessStopResult(service.Id, pending.Id, operation.Result));
        else _stopTasks[service.Id] = AwaitStop(operation, service.Id, pending.Id);
    }

    private async Task AwaitStop(Task<bool> operation, uint serviceId, Guid operationId)
    {
        bool success = false;
        try { success = await operation.ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Stop operation failed for service {Id}.", serviceId); }
        var result = new ProcessStopResult(serviceId, operationId, success);
        if (_stopCompletion is { } complete) complete(result);
        else _stopResults.Enqueue(result);
    }

    private void DrainStopResults()
    {
        while (_stopResults.TryDequeue(out var result)) CompleteStop(result);
    }

    /// <summary>Called on the batch task, after the process wait completes outside it.</summary>
    public void CompleteStop(ProcessStopResult result)
    {
        if (!_pendingStops.TryGetValue(result.ServiceId, out var pending) || pending.Id != result.OperationId) return;
        _pendingStops.Remove(result.ServiceId);
        _stopTasks.Remove(result.ServiceId);
        var service = pending.Service;
        if (result.Success)
        {
            SetRestartIntent(service, false);
            if (service.IsWorld)
                foreach (var member in _state.Services.Values.Where(s => s.Group.GroupId == service.Group.GroupId))
                    SetRestartIntent(member, false);
            service.Conn?.Conn.Close();
            SetServiceStatus(service, Proto.SvcStopped, 0);
        }
        else SetServiceStatus(service, _svcCtl.GetStatus(service) ?? Proto.SvcRunning, 0);
        SendControlResult(pending.Manager, result.Success);
    }

    private static void SendControlResult(ManagerSession manager, bool success)
        => manager.Send(new PacketWriter(Msg.CT_SERVICECONTROL_ACK).WriteByte(success ? (byte)1 : (byte)0));

    private void SetRestartIntent(ServiceInstance service, bool enabled)
    {
        service.ManagerControl = enabled;
        try { _svcCtl.SetRestartIntent(service, enabled); }
        catch (Exception ex) { _log.LogError(ex, "Could not persist restart intent for {Name}.", service.Name); }
    }

    public async Task StopOperationsAsync()
    {
        _operationCancellation.Cancel();
        await Task.WhenAll(_stopTasks.Values.ToArray());
        _operationCancellation.Dispose();
    }
}
