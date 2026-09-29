using System.Net;
using System.Text.Json;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;

namespace VspcAutotaskPlugin.Vspc;

/// <summary>
/// Client for the Veeam Service Provider Console REST API v3 (VSPC 9.2 / API 3.6.2).
/// The connection is auto-provisioned by the VSPC plugin host (loopback endpoint via
/// discoverService, plugin API key via getApiKey), so every call authenticates with a
/// bearer API key. Handles the standard envelope + limit/offset paging, JSON filter
/// expressions, 429 retry and 202 async-action polling.
/// </summary>
public sealed class VspcClient
{
    public const string SettingsKey = "vspc.connection";
    private const string ClientVersion = "3.6.2";

    private readonly Db _db;
    private readonly SecretProtector _protector;
    private readonly ILogger<VspcClient> _logger;

    private static readonly HttpClient HttpStrict = new(new SocketsHttpHandler(), disposeHandler: false)
    { Timeout = TimeSpan.FromSeconds(100) };

    private static readonly HttpClient HttpInsecure = new(new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    }, disposeHandler: false)
    { Timeout = TimeSpan.FromSeconds(100) };

    public VspcClient(Db db, SecretProtector protector, ILogger<VspcClient> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public VspcConnectionSettings? GetSettings() => _db.GetJson<VspcConnectionSettings>(SettingsKey);

    public bool IsConfigured => GetSettings() is { BaseUrl.Length: > 0 } s && !string.IsNullOrEmpty(s.ApiKeyEnc);

    private static HttpClient Http(VspcConnectionSettings s) => s.IgnoreTlsErrors ? HttpInsecure : HttpStrict;

    private static string ApiUrl(VspcConnectionSettings s, string path) =>
        s.BaseUrl.TrimEnd('/') + "/api/v3" + path;

    private string Bearer(VspcConnectionSettings s)
    {
        var key = _protector.Unprotect(s.ApiKeyEnc ?? "");
        if (string.IsNullOrEmpty(key))
            throw new VspcApiException(401, "VSPC API key has not been provisioned by the plugin host yet");
        return key;
    }

    // ------------------------------------------------------------- transport

    private async Task<string> SendAsync(
        HttpMethod method, string path, HttpContent? content = null, CancellationToken ct = default)
    {
        var settings = GetSettings()
            ?? throw new VspcApiException(400, "VSPC connection has not been provisioned by the plugin host yet");

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, ApiUrl(settings, path));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Bearer(settings));
            request.Headers.TryAddWithoutValidation("x-client-version", ClientVersion);
            if (content != null) request.Content = content;

            using var response = await Http(settings).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if ((int)response.StatusCode == 429 && attempt <= 3)
            {
                var delay = TimeSpan.FromSeconds(5);
                try
                {
                    var env = JsonSerializer.Deserialize<VspcEnvelope<object>>(body, Db.Json);
                    var retryAfter = env?.Errors?.FirstOrDefault()?.RetryAfter;
                    if (retryAfter is > 0 and < 300) delay = TimeSpan.FromSeconds(retryAfter.Value);
                }
                catch { /* fall back to default delay */ }
                _logger.LogWarning("VSPC throttled request to {Path}; retrying in {Delay}s", path, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var location = response.Headers.Location?.ToString()
                    ?? throw new VspcApiException(202, "VSPC accepted the request asynchronously but returned no Location header");
                return await PollAsyncActionAsync(settings, location, ct);
            }

            if (!response.IsSuccessStatusCode)
                throw new VspcApiException((int)response.StatusCode, ExtractErrors(body, response.StatusCode));

            return body;
        }
    }

    private async Task<string> PollAsyncActionAsync(VspcConnectionSettings settings, string location, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Bearer(settings));
            request.Headers.TryAddWithoutValidation("x-client-version", ClientVersion);
            using var response = await Http(settings).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new VspcApiException((int)response.StatusCode, ExtractErrors(body, response.StatusCode));

            string? status = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("status", out var st))
                    status = st.GetString();
            }
            catch { /* keep polling */ }

            if (status == null) continue;
            if (status.Contains("succe", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("done", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("complet", StringComparison.OrdinalIgnoreCase))
            {
                using var resultRequest = new HttpRequestMessage(HttpMethod.Get, location.TrimEnd('/') + "/result");
                resultRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Bearer(settings));
                resultRequest.Headers.TryAddWithoutValidation("x-client-version", ClientVersion);
                using var resultResponse = await Http(settings).SendAsync(resultRequest, ct);
                var resultBody = await resultResponse.Content.ReadAsStringAsync(ct);
                if (!resultResponse.IsSuccessStatusCode)
                    throw new VspcApiException((int)resultResponse.StatusCode, ExtractErrors(resultBody, resultResponse.StatusCode));
                return resultBody;
            }
            if (status.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("error", StringComparison.OrdinalIgnoreCase))
                throw new VspcApiException(520, $"VSPC async action ended with status '{status}'");
        }
        throw new VspcApiException(504, "Timed out waiting for VSPC async action to complete");
    }

    private static string ExtractErrors(string body, HttpStatusCode status)
    {
        try
        {
            var env = JsonSerializer.Deserialize<VspcEnvelope<object>>(body, Db.Json);
            var messages = env?.Errors?.Select(e => e.Message).Where(m => !string.IsNullOrEmpty(m)).ToList();
            if (messages is { Count: > 0 })
                return $"VSPC API error ({(int)status}): {string.Join("; ", messages)}";
        }
        catch { /* not an envelope */ }
        var snippet = body.Length > 300 ? body[..300] : body;
        return $"VSPC API error ({(int)status}): {snippet}";
    }

    private async Task<T> GetDataAsync<T>(string path, CancellationToken ct = default) where T : class
    {
        var body = await SendAsync(HttpMethod.Get, path, ct: ct);
        var envelope = JsonSerializer.Deserialize<VspcEnvelope<T>>(body, Db.Json);
        return envelope?.Data ?? throw new VspcApiException(500, $"VSPC returned an empty payload for {path}");
    }

    public async Task<List<T>> GetPagedAsync<T>(string pathWithQuery, int pageSize = 200, CancellationToken ct = default)
    {
        var results = new List<T>();
        var offset = 0;
        while (true)
        {
            var separator = pathWithQuery.Contains('?') ? '&' : '?';
            var page = $"{pathWithQuery}{separator}limit={pageSize}&offset={offset}";
            var body = await SendAsync(HttpMethod.Get, page, ct: ct);
            var envelope = JsonSerializer.Deserialize<VspcEnvelope<List<T>>>(body, Db.Json);
            var items = envelope?.Data ?? new List<T>();
            results.AddRange(items);
            var total = envelope?.Meta?.PagingInfo?.Total;
            offset += items.Count;
            if (items.Count < pageSize || (total.HasValue && offset >= total.Value) || items.Count == 0)
                break;
        }
        return results;
    }

    private static string FilterQuery(string property, string operation, string value)
    {
        var filter = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string>
            {
                ["property"] = property,
                ["operation"] = operation,
                ["value"] = value
            }
        });
        return "filter=" + Uri.EscapeDataString(filter);
    }

    // ----------------------------------------------------------- public API

    public async Task<VspcUser?> GetUserAsync(string userUid, CancellationToken ct = default)
    {
        try
        {
            return await GetDataAsync<VspcUser>($"/users/{userUid}", ct);
        }
        catch (VspcApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Identifies the caller of a proxied UI request by presenting the user's own VSPC UI
    /// bearer token to GET /users/me. Returns null when VSPC rejects the token.
    /// </summary>
    public async Task<VspcUser?> GetUserByBearerAsync(string bearerToken, CancellationToken ct = default)
    {
        var settings = GetSettings()
            ?? throw new VspcApiException(400, "VSPC connection has not been provisioned by the plugin host yet");
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl(settings, "/users/me"));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerToken);
        request.Headers.TryAddWithoutValidation("x-client-version", ClientVersion);
        using var response = await Http(settings).SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new VspcApiException((int)response.StatusCode, ExtractErrors(body, response.StatusCode));
        return JsonSerializer.Deserialize<VspcEnvelope<VspcUser>>(body, Db.Json)?.Data;
    }

    public Task<List<VspcCompany>> GetCompaniesAsync(CancellationToken ct = default) =>
        GetPagedAsync<VspcCompany>("/organizations/companies", ct: ct);

    public Task<List<VspcAlarmTemplate>> GetAlarmTemplatesAsync(CancellationToken ct = default) =>
        GetPagedAsync<VspcAlarmTemplate>("/alarms/templates", ct: ct);

    /// <summary>Triggered alarms; when <paramref name="changedSince"/> is set, only activations after that moment.</summary>
    public Task<List<VspcActiveAlarm>> GetActiveAlarmsAsync(DateTimeOffset? changedSince = null, CancellationToken ct = default)
    {
        var path = "/alarms/active";
        if (changedSince.HasValue)
            path += "?" + FilterQuery("lastActivation.time", "greaterThan",
                changedSince.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        return GetPagedAsync<VspcActiveAlarm>(path, ct: ct);
    }

    public async Task<VspcActiveAlarm?> GetActiveAlarmAsync(string activeAlarmUid, CancellationToken ct = default)
    {
        try
        {
            return await GetDataAsync<VspcActiveAlarm>($"/alarms/active/{activeAlarmUid}", ct);
        }
        catch (VspcApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task ResolveActiveAlarmAsync(string activeAlarmUid, string comment, bool resolveOnClients = false, CancellationToken ct = default)
    {
        if (comment.Length > 512) comment = comment[..512];
        var path = $"/alarms/active/{activeAlarmUid}/resolve" +
                   $"?comment={Uri.EscapeDataString(comment)}&resolveOnClients={(resolveOnClients ? "true" : "false")}";
        await SendAsync(HttpMethod.Post, path, ct: ct);
    }

    public Task<List<VspcCompanyUsage>> GetCompanyUsageAsync(string companyUid, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var path = $"/organizations/companies/{companyUid}/usage" +
                   $"?fromDate={from:yyyy-MM-dd}&toDate={to:yyyy-MM-dd}";
        return GetPagedAsync<VspcCompanyUsage>(path, ct: ct);
    }

    public Task<List<VspcSubscriptionPlan>> GetSubscriptionPlansAsync(CancellationToken ct = default) =>
        GetPagedAsync<VspcSubscriptionPlan>("/subscriptionPlans", ct: ct);

    /// <summary>Generic POST for endpoints whose schemas vary across VSPC builds (e.g. company creation).</summary>
    public async Task<string> PostRawAsync(string path, object payload, CancellationToken ct = default)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload, Db.Json),
            System.Text.Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, path, content, ct);
    }
}
