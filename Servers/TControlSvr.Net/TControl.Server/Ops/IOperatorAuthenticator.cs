using TControl.Data;

namespace TControl.Server.Ops;

public interface IOperatorAuthenticator
{
    Task<int> AuthenticateAsync(string id, string password, CancellationToken cancellationToken);
}

/// <summary>No database means no operator access. Tests inject an explicit fake instead.</summary>
public sealed class DatabaseOperatorAuthenticator(ControlDatabase? database) : IOperatorAuthenticator
{
    public Task<int> AuthenticateAsync(string id, string password, CancellationToken cancellationToken)
        => database is null ? Task.FromResult(0) : database.OpLoginAsync(id, password, cancellationToken);
}
