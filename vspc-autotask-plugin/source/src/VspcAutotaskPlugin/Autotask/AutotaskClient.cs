using System.Text;
using System.Text.Json;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;

namespace VspcAutotaskPlugin.Autotask;

/// <summary>
/// Client for the Autotask PSA REST API (v1.0). Handles zone discovery, auth headers,
/// query paging via nextPageUrl, picklist metadata caching, and 429 thread-limit retries.
/// Writes are sent as dictionaries with exact Autotask field names.
/// </summary>
public sealed class AutotaskClient
{
    public const string SettingsKey = "autotask.connection";
    private const string GlobalZoneInfoUrl =
        "https://webservices.autotask.net/atservicesrest/v1.0/zoneInformation";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    private readonly Db _db;
    private readonly SecretProtector _protector;
    private readonly ILogger<AutotaskClient> _logger;
    // Autotask allows ~3 concurrent threads per endpoint; a global gate of 2 keeps us clear.
    private readonly SemaphoreSlim _gate = new(2, 2);

    public AutotaskClient(Db db, SecretProtector protector, ILogger<AutotaskClient> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public AutotaskConnectionSettings? GetSettings() => _db.GetJson<AutotaskConnectionSettings>(SettingsKey);

    public bool IsConfigured => GetSettings() is { Username.Length: > 0, ZoneUrl.Length: > 0 };

    // ------------------------------------------------- live connection state

    public sealed record ConnectionCheck(bool Ok, string? Error, DateTimeOffset At);

    private static readonly TimeSpan CheckCacheAge = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private ConnectionCheck? _lastCheck;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <summary>Drops the cached connection check (call after settings change).</summary>
    public void InvalidateConnectionCheck() => _lastCheck = null;

    /// <summary>
    /// Live "is Autotask actually reachable with the stored credentials" check, driving the
    /// Dashboard connection tile. Cached for a minute (the dashboard polls status) and capped
    /// at a short timeout so an unreachable Autotask can't stall the status endpoint.
    /// </summary>
    public async Task<ConnectionCheck> CheckConnectionCachedAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ConnectionCheck(false, null, DateTimeOffset.UtcNow);

        var cached = _lastCheck;
        if (cached != null && DateTimeOffset.UtcNow - cached.At < CheckCacheAge)
            return cached;

        await _checkGate.WaitAsync(ct);
        try
        {
            cached = _lastCheck;
            if (cached != null && DateTimeOffset.UtcNow - cached.At < CheckCacheAge)
                return cached;

            ConnectionCheck result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);
            try
            {
                await QueryCountAsync("Companies", AtQuery.Eq("isActive", true), timeout.Token);
                result = new ConnectionCheck(true, null, DateTimeOffset.UtcNow);
            }
            catch (AtApiException ex)
            {
                result = new ConnectionCheck(false, ex.Message, DateTimeOffset.UtcNow);
            }
            catch (HttpRequestException ex)
            {
                result = new ConnectionCheck(false, "Could not reach Autotask: " + ex.Message, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = new ConnectionCheck(false, "Connection to Autotask timed out", DateTimeOffset.UtcNow);
            }
            _lastCheck = result;
            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    // ------------------------------------------------------------------ zone

    public async Task<AtZoneInfo> ResolveZoneAsync(string username, CancellationToken ct = default)
    {
        var url = $"{GlobalZoneInfoUrl}?user={Uri.EscapeDataString(username)}";
        using var response = await Http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AtApiException((int)response.StatusCode, ExtractErrors(body, (int)response.StatusCode));
        var zone = JsonSerializer.Deserialize<AtZoneInfo>(body, Db.Json);
        if (string.IsNullOrEmpty(zone?.Url))
            throw new AtApiException(500, "Autotask zone information response did not include a zone URL");
        return zone;
    }

    private string BaseUrl(AutotaskConnectionSettings s)
    {
        if (string.IsNullOrEmpty(s.ZoneUrl))
            throw new AtApiException(400, "Autotask zone URL is not resolved yet");
        return s.ZoneUrl.TrimEnd('/') + "/v1.0/";
    }

    // ------------------------------------------------------------- transport

    private async Task<string> SendAsync(
        HttpMethod method, string url, object? jsonBody = null,
        AutotaskConnectionSettings? overrideSettings = null, CancellationToken ct = default)
    {
        var settings = overrideSettings ?? GetSettings()
            ?? throw new AtApiException(400, "Autotask connection is not configured");

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("UserName", settings.Username);
            request.Headers.TryAddWithoutValidation("Secret", _protector.Unprotect(settings.SecretEnc ?? ""));
            request.Headers.TryAddWithoutValidation("ApiIntegrationCode", settings.IntegrationCode);
            if (jsonBody != null)
                request.Content = new StringContent(
                    JsonSerializer.Serialize(jsonBody, Db.Json), Encoding.UTF8, "application/json");

            await _gate.WaitAsync(ct);
            HttpResponseMessage response;
            string body;
            try
            {
                response = await Http.SendAsync(request, ct);
                body = await response.Content.ReadAsStringAsync(ct);
            }
            finally
            {
                _gate.Release();
            }

            using (response)
            {
                if ((int)response.StatusCode == 429 && attempt <= 4)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s, 16s
                    _logger.LogWarning("Autotask throttled {Url}; retrying in {Delay}s", url, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new AtApiException((int)response.StatusCode, ExtractErrors(body, (int)response.StatusCode));

                return body;
            }
        }
    }

    private static string ExtractErrors(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var messages = errors.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                    .Where(m => !string.IsNullOrEmpty(m));
                var joined = string.Join("; ", messages);
                if (joined.Length > 0)
                    return $"Autotask API error ({status}): {joined}";
            }
        }
        catch { /* not JSON */ }
        var snippet = body.Length > 300 ? body[..300] : body;
        return $"Autotask API error ({status}): {snippet}";
    }

    // ----------------------------------------------------------------- reads

    /// <summary>Runs an entity query and follows nextPageUrl until exhausted (or maxTotal reached).</summary>
    public async Task<List<T>> QueryAsync<T>(
        string entity, object filter, string[]? includeFields = null,
        int maxTotal = 10_000, CancellationToken ct = default)
    {
        var results = new List<T>();
        var body = AtQuery.Body(filter, includeFields: includeFields);
        var settings = GetSettings() ?? throw new AtApiException(400, "Autotask connection is not configured");
        var url = BaseUrl(settings) + entity + "/query";

        while (true)
        {
            var responseBody = await SendAsync(HttpMethod.Post, url, body, ct: ct);
            var page = JsonSerializer.Deserialize<AtQueryResponse<T>>(responseBody, Db.Json);
            if (page?.Items != null)
                results.AddRange(page.Items);
            var next = page?.PageDetails?.NextPageUrl;
            if (string.IsNullOrEmpty(next) || results.Count >= maxTotal)
                return results;
            url = next; // next page: same POST body against the provided URL
        }
    }

    public async Task<long> QueryCountAsync(string entity, object filter, CancellationToken ct = default)
    {
        var settings = GetSettings() ?? throw new AtApiException(400, "Autotask connection is not configured");
        var url = BaseUrl(settings) + entity + "/query/count";
        var body = await SendAsync(HttpMethod.Post, url, new Dictionary<string, object?>
        {
            ["filter"] = filter as object[] ?? new[] { filter }
        }, ct: ct);
        return JsonSerializer.Deserialize<AtCountResponse>(body, Db.Json)?.QueryCount ?? 0;
    }

    public async Task<T?> GetAsync<T>(string entity, long id, CancellationToken ct = default) where T : class
    {
        var settings = GetSettings() ?? throw new AtApiException(400, "Autotask connection is not configured");
        try
        {
            var body = await SendAsync(HttpMethod.Get, BaseUrl(settings) + entity + "/" + id, ct: ct);
            return JsonSerializer.Deserialize<AtSingleResponse<T>>(body, Db.Json)?.Item;
        }
        catch (AtApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- writes

    public async Task<long> CreateAsync(string entity, Dictionary<string, object?> payload, CancellationToken ct = default)
    {
        var settings = GetSettings() ?? throw new AtApiException(400, "Autotask connection is not configured");
        var body = await SendAsync(HttpMethod.Post, BaseUrl(settings) + entity, payload, ct: ct);
        return JsonSerializer.Deserialize<AtItemResponse>(body, Db.Json)?.ItemId
               ?? throw new AtApiException(500, $"Autotask create on {entity} returned no itemId");
    }

    /// <summary>PATCH update — target id goes in the payload, only provided fields change.</summary>
    public async Task PatchAsync(string entity, Dictionary<string, object?> payload, CancellationToken ct = default)
    {
        var settings = GetSettings() ?? throw new AtApiException(400, "Autotask connection is not configured");
        await SendAsync(HttpMethod.Patch, BaseUrl(settings) + entity, payload, ct: ct);
    }

    // ------------------------------------------------------------- metadata

    private sealed class CachedFields
    {
        public DateTimeOffset FetchedAt { get; set; }
        public List<AtFieldInfo> Fields { get; set; } = new();
    }

    /// <summary>Entity field metadata incl. picklist values, cached for 24h.</summary>
    public async Task<List<AtFieldInfo>> GetEntityFieldsAsync(string entity, bool forceRefresh = false, CancellationToken ct = default)
    {
        var cacheKey = "at.fields." + entity;
        if (!forceRefresh)
        {
            var cached = _db.GetSyncJson<CachedFields>(cacheKey);
            if (cached != null && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromHours(24))
                return cached.Fields;
        }

        var settings = GetSettings() ?? throw new AtApiException(400, "Autotask connection is not configured");
        var body = await SendAsync(HttpMethod.Get, BaseUrl(settings) + entity + "/entityInformation/fields", ct: ct);
        var fields = JsonSerializer.Deserialize<AtFieldsResponse>(body, Db.Json)?.Fields ?? new List<AtFieldInfo>();
        _db.SetSyncJson(cacheKey, new CachedFields { FetchedAt = DateTimeOffset.UtcNow, Fields = fields });
        return fields;
    }

    public async Task<List<AtPicklistValue>> GetPicklistAsync(string entity, string fieldName, CancellationToken ct = default)
    {
        // Picklists back UI dropdowns (ticket queue/status/priority, service period type);
        // return empty until the connection is configured so those pages render cleanly.
        if (!IsConfigured) return new List<AtPicklistValue>();
        var fields = await GetEntityFieldsAsync(entity, ct: ct);
        return fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                   ?.PicklistValues?.Where(v => v.IsActive).OrderBy(v => v.SortOrder ?? 0).ToList()
               ?? new List<AtPicklistValue>();
    }

    // ------------------------------------------------------------ connection

    /// <summary>Validates candidate credentials; resolves the zone when not cached. Returns updated settings on success.</summary>
    public async Task<(bool Ok, string Message, AutotaskConnectionSettings Settings)> TestConnectionAsync(
        AutotaskConnectionSettings candidate, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(candidate.ZoneUrl))
            {
                var zone = await ResolveZoneAsync(candidate.Username, ct);
                candidate.ZoneUrl = zone.Url;
            }

            var url = BaseUrl(candidate) + "Companies/query/count";
            var body = await SendAsync(HttpMethod.Post, url, new Dictionary<string, object?>
            {
                ["filter"] = new object[] { AtQuery.Eq("isActive", true) }
            }, overrideSettings: candidate, ct: ct);
            var count = JsonSerializer.Deserialize<AtCountResponse>(body, Db.Json)?.QueryCount ?? 0;
            return (true, $"Connected to Autotask ({candidate.ZoneUrl}) — {count} active companies visible", candidate);
        }
        catch (AtApiException ex)
        {
            return (false, ex.Message, candidate);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach Autotask: {ex.Message}", candidate);
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection to Autotask timed out", candidate);
        }
    }

    // ------------------------------------------------------- entity helpers
    //
    // These are the accessors the plugin UI drives directly to fill grids and dropdowns.
    // Before the Autotask connection is configured they return empty rather than throwing:
    // the pages then render clean empty states (no Autotask company to map to yet, no
    // services/billing-codes/contracts to choose) instead of surfacing the raw
    // "Autotask connection is not configured" error. The low-level QueryAsync stays strict.

    public Task<List<AtCompany>> GetActiveCompaniesAsync(CancellationToken ct = default) =>
        IsConfigured
            ? QueryAsync<AtCompany>("Companies", AtQuery.Eq("isActive", true),
                new[] { "id", "companyName", "companyType", "isActive" }, ct: ct)
            : Task.FromResult(new List<AtCompany>());

    public Task<List<AtContract>> GetCompanyContractsAsync(long companyId, CancellationToken ct = default) =>
        IsConfigured
            ? QueryAsync<AtContract>("Contracts", AtQuery.Eq("companyID", companyId), ct: ct)
            : Task.FromResult(new List<AtContract>());

    public Task<List<AtService>> GetActiveServicesAsync(CancellationToken ct = default) =>
        IsConfigured
            ? QueryAsync<AtService>("Services", AtQuery.Eq("isActive", true), ct: ct)
            : Task.FromResult(new List<AtService>());

    public Task<List<AtBillingCode>> GetBillingCodesAsync(CancellationToken ct = default) =>
        IsConfigured
            ? QueryAsync<AtBillingCode>("BillingCodes", AtQuery.Eq("isActive", true), ct: ct)
            : Task.FromResult(new List<AtBillingCode>());

    public Task<List<AtContractService>> GetContractServicesAsync(long contractId, CancellationToken ct = default) =>
        QueryAsync<AtContractService>("ContractServices", AtQuery.Eq("contractID", contractId), ct: ct);

    /// <summary>Current effective unit count for a contract service (record covering today).</summary>
    public async Task<long> GetCurrentUnitsAsync(long contractServiceId, DateTime todayUtc, CancellationToken ct = default)
    {
        var rows = await QueryAsync<AtContractServiceUnit>("ContractServiceUnits",
            AtQuery.And(
                AtQuery.Eq("contractServiceID", contractServiceId),
                AtQuery.Lte("startDate", todayUtc.ToString("yyyy-MM-ddT00:00:00")),
                AtQuery.Gte("endDate", todayUtc.ToString("yyyy-MM-ddT00:00:00"))), ct: ct);
        // Zero-unit periods return no rows; overlapping rows should not occur, but sum defensively.
        return rows.Sum(r => r.Units);
    }
}
