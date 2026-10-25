namespace TLogin.Data;

public interface ILoginAuthenticationStore
{
    Task<int> CheckIpAsync(string ip, CancellationToken ct = default);
    Task<LoginRow> LoginAsync(string userId, string password, string loginIp, byte ipCheck, CancellationToken ct = default);
    Task<LoginRow> LoginJpAsync(string userId, string password, string loginIp, byte ipCheck, byte channeling, CancellationToken ct = default);
    Task<int> CheckPasswordAsync(uint userId, string password, CancellationToken ct = default);
    Task ReleaseCurrentUserAsync(uint userId, CancellationToken ct = default);
    Task<uint> ValidateCredentialsAsync(string userName, string password, CancellationToken ct = default);
}
