using System.Collections.Concurrent;
using Grpc.Net.Client;
using Veeam.AC.Service.PluginManager;
using Veeam.AC.Service.Users;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Plugin;

/// <summary>
/// Authenticates requests proxied by the VSPC UI. VSPC adds an X-User-Uid header for
/// portal-authenticated users; the user's role is resolved primarily through the SDK's
/// gRPC UserService (discovered via the plugin registry, not subject to REST permissions),
/// with the VSPC REST API as fallback. Only administrative portal roles are admitted.
/// </summary>
public sealed class PluginAuthService
{
    public const string UserUidHeader = "X-User-Uid";
    public const string AccessTokenHeader = "X-Vspc-Access-Token";
    private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(30);

    // SDK Users.proto roles: "administrator" = full system administrator, "operator" = system operator.
    private static readonly HashSet<UserRole> AllowedGrpcRoles = new() { UserRole.Administrator, UserRole.Operator };
    // REST fallback accepts both documented-portal and proto-style role namings.
    private static readonly HashSet<string> AllowedRestRoles = new(StringComparer.OrdinalIgnoreCase)
    { "PortalAdministrator", "PortalOperator", "Administrator", "Operator" };

    private readonly PluginContext _context;
    private readonly PluginRegistry.PluginRegistryClient _registry;
    private readonly VspcClient _vspc;
    private readonly ILogger<PluginAuthService> _logger;
    private readonly ConcurrentDictionary<string, (bool Ok, DateTimeOffset Until)> _cache = new();
    private readonly SemaphoreSlim _usersLock = new(1, 1);
    private UserService.UserServiceClient? _users;

    public PluginAuthService(PluginContext context, PluginRegistry.PluginRegistryClient registry,
        VspcClient vspc, ILogger<PluginAuthService> logger)
    {
        _context = context;
        _registry = registry;
        _vspc = vspc;
        _logger = logger;
    }

    public async Task<bool> IsAuthorizedAsync(HttpContext ctx)
    {
        // Credential 1: X-User-Uid header injected by the VSPC proxy (per the template guide).
        var uid = ctx.Request.Headers[UserUidHeader].ToString();
        if (!string.IsNullOrEmpty(uid))
        {
            if (_cache.TryGetValue(uid, out var cached) && DateTimeOffset.UtcNow < cached.Until)
                return cached.Ok;

            var (ok, source, role) = await ResolveAsync(uid);
            if (ok)
                _logger.LogInformation("Authorized VSPC user {Uid} with role '{Role}' (via {Source})", uid, role, source);
            else
                _logger.LogWarning("Rejected VSPC user {Uid}: role '{Role}' (via {Source})", uid, role, source);

            _cache[uid] = (ok, DateTimeOffset.UtcNow + (ok ? PositiveCacheTtl : NegativeCacheTtl));
            return ok;
        }

        // Credential 2: the signed-in portal user's own VSPC token. The SPA sends it in a
        // custom header because the VSPC proxy rejects Authorization headers on plugin
        // routes with 403 before they reach the service; plain Authorization is still
        // accepted for direct (non-proxied) access.
        var token = ctx.Request.Headers[AccessTokenHeader].ToString();
        if (string.IsNullOrEmpty(token))
        {
            var authorization = ctx.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = authorization[7..];
        }
        if (!string.IsNullOrEmpty(token))
            return await AuthorizeByBearerAsync(token.Trim());

        return false;
    }

    private async Task<bool> AuthorizeByBearerAsync(string token)
    {
        if (token.Length == 0) return false;
        var cacheKey = "bearer:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))[..24];
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow < cached.Until)
            return cached.Ok;

        bool ok;
        string role = "(unknown)", source = "bearer /users/me";
        try
        {
            var user = await _vspc.GetUserByBearerAsync(token);
            if (user == null)
            {
                ok = false;
                role = "token rejected by VSPC";
            }
            else
            {
                role = user.Role ?? "(null)";
                ok = user.Role != null && AllowedRestRoles.Contains(user.Role);

                // REST role naming is deployment-uncertain; confirm via the plugin
                // framework's gRPC UserService before rejecting.
                if (!ok && !string.IsNullOrEmpty(user.InstanceUid))
                {
                    try
                    {
                        var users = await EnsureUsersClientAsync();
                        if (users != null && _context.Manifest != null)
                        {
                            var reply = await users.getUserAsync(new UserRequest
                            {
                                PluginId = _context.Manifest.PluginId,
                                InstanceUid = user.InstanceUid
                            });
                            var grpcRole = reply.User?.Role ?? UserRole.UnknownRole;
                            role += " / gRPC:" + grpcRole;
                            ok = AllowedGrpcRoles.Contains(grpcRole);
                            source = "bearer + gRPC UserService";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "gRPC role confirmation failed for bearer user {Uid}", user.InstanceUid);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bearer-token user lookup failed");
            ok = false;
            role = "lookup failed";
        }

        if (ok)
            _logger.LogInformation("Authorized VSPC portal user with role '{Role}' (via {Source})", role, source);
        else
            _logger.LogWarning("Rejected bearer credential: role '{Role}' (via {Source})", role, source);

        _cache[cacheKey] = (ok, DateTimeOffset.UtcNow + (ok ? PositiveCacheTtl : NegativeCacheTtl));
        return ok;
    }

    private async Task<(bool Ok, string Source, string Role)> ResolveAsync(string uid)
    {
        // Primary: the plugin framework's own user service.
        try
        {
            var users = await EnsureUsersClientAsync();
            if (users != null && _context.Manifest != null)
            {
                var reply = await users.getUserAsync(new UserRequest
                {
                    PluginId = _context.Manifest.PluginId,
                    InstanceUid = uid
                });
                var role = reply.User?.Role ?? UserRole.UnknownRole;
                return (AllowedGrpcRoles.Contains(role), "gRPC UserService", role.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gRPC user lookup failed for {Uid}; falling back to REST", uid);
        }

        // Fallback: VSPC REST API with the plugin's API key.
        try
        {
            var user = await _vspc.GetUserAsync(uid);
            if (user == null)
                return (false, "REST", "user not found");
            return (user.Role != null && AllowedRestRoles.Contains(user.Role), "REST", user.Role ?? "(null)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REST user lookup failed for {Uid}", uid);
            return (false, "none", "lookup failed");
        }
    }

    private async Task<UserService.UserServiceClient?> EnsureUsersClientAsync()
    {
        if (_users != null) return _users;
        if (_context.Manifest == null) return null;

        await _usersLock.WaitAsync();
        try
        {
            if (_users != null) return _users;
            var reply = await _registry.discoverServiceAsync(new PluginDiscoverServiceRequest
            {
                PluginId = _context.Manifest.PluginId,
                Service = VspcServiceName.Users
            });
            if (string.IsNullOrEmpty(reply.ServiceEndpoint))
                return null;

            var channel = GrpcChannel.ForAddress(reply.ServiceEndpoint, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    // Loopback endpoint with a VSPC-managed certificate when TLS is used.
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, _, _, _) => true
                    }
                }
            });
            _users = new UserService.UserServiceClient(channel);
            _logger.LogInformation("VSPC Users gRPC service discovered at {Endpoint}", reply.ServiceEndpoint);
            return _users;
        }
        finally
        {
            _usersLock.Release();
        }
    }
}
