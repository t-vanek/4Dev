using TMap.Data;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    public IOperatorPostStore? OperatorPostStore { get; set; }
    public Action<OperatorPostIntent>? OperatorPostEnqueue { get; set; }
    public Func<OperatorPostIntent, Task>? LegacyOperatorPostForward { get; set; }

    public Task VerifyOperatorPostStorageAsync() => OperatorPostStore?.VerifyOperatorPostSchemaAsync() ?? Task.CompletedTask;
}
