using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Storage;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Sync;

public sealed class CompanySnapshot
{
    public DateTimeOffset FetchedAt { get; set; }
    public List<VspcCompany> VspcCompanies { get; set; } = new();
    public List<AtCompany> AtCompanies { get; set; } = new();
}

public sealed record MatchCandidate(long AtCompanyId, string AtCompanyName, double Score, bool Exact);

public sealed record CompanyMatchSuggestion(string VspcCompanyUid, string VspcCompanyName, List<MatchCandidate> Candidates);

/// <summary>
/// Company synchronization: caches both company lists, auto-maps exact name matches,
/// suggests fuzzy candidates, and handles manual map/unmap — the Autotask counterpart
/// of the CWM plugin's Companies feature.
/// </summary>
public sealed class CompanySyncService
{
    private const string SnapshotKey = "companies.snapshot";
    public const double SuggestionThreshold = 0.72;

    private readonly VspcClient _vspc;
    private readonly AutotaskClient _autotask;
    private readonly Store _store;
    private readonly Db _db;
    private readonly ActivityLog _activity;

    public CompanySyncService(VspcClient vspc, AutotaskClient autotask, Store store, Db db, ActivityLog activity)
    {
        _vspc = vspc;
        _autotask = autotask;
        _store = store;
        _db = db;
        _activity = activity;
    }

    public CompanySnapshot? GetSnapshot() => _db.GetSyncJson<CompanySnapshot>(SnapshotKey);

    public async Task<CompanySnapshot> RefreshAsync(CancellationToken ct = default)
    {
        var vspcTask = _vspc.GetCompaniesAsync(ct);
        var atTask = _autotask.GetActiveCompaniesAsync(ct);
        await Task.WhenAll(vspcTask, atTask);

        var snapshot = new CompanySnapshot
        {
            FetchedAt = DateTimeOffset.UtcNow,
            VspcCompanies = vspcTask.Result,
            AtCompanies = atTask.Result
        };
        _db.SetSyncJson(SnapshotKey, snapshot);
        _activity.Info("companies",
            $"Refreshed company lists: {snapshot.VspcCompanies.Count} VSPC companies, {snapshot.AtCompanies.Count} Autotask companies");

        // Keep stored mapping display names in sync with renamed companies.
        var byUid = snapshot.VspcCompanies.ToDictionary(c => c.InstanceUid, c => c.Name, StringComparer.OrdinalIgnoreCase);
        var byId = snapshot.AtCompanies.ToDictionary(c => c.Id, c => c.CompanyName ?? "");
        foreach (var mapping in _store.GetCompanyMappings())
        {
            var vspcName = byUid.TryGetValue(mapping.VspcCompanyUid, out var vn) ? vn : mapping.VspcCompanyName;
            var atName = byId.TryGetValue(mapping.AtCompanyId, out var an) ? an : mapping.AtCompanyName;
            if (vspcName != mapping.VspcCompanyName || atName != mapping.AtCompanyName)
                _store.UpsertCompanyMapping(mapping with { VspcCompanyName = vspcName, AtCompanyName = atName });
        }
        return snapshot;
    }

    public async Task<CompanySnapshot> GetOrRefreshAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var snapshot = GetSnapshot();
        if (snapshot != null && DateTimeOffset.UtcNow - snapshot.FetchedAt < maxAge)
            return snapshot;
        return await RefreshAsync(ct);
    }

    /// <summary>
    /// Exact normalized-name matches are mapped automatically (when <paramref name="apply"/>);
    /// near matches above the threshold are returned as suggestions for manual confirmation.
    /// </summary>
    public (int AutoMapped, List<CompanyMatchSuggestion> Suggestions) AutoMap(CompanySnapshot snapshot, bool apply)
    {
        var mappedVspc = _store.GetCompanyMappings().Select(m => m.VspcCompanyUid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedAt = _store.GetCompanyMappings().Select(m => m.AtCompanyId).ToHashSet();

        var unmappedVspc = snapshot.VspcCompanies.Where(c => !mappedVspc.Contains(c.InstanceUid)).ToList();
        var availableAt = snapshot.AtCompanies.Where(c => !mappedAt.Contains(c.Id)).ToList();
        var atByNormalized = availableAt
            .GroupBy(c => NameMatcher.Normalize(c.CompanyName ?? ""))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList());

        var autoMapped = 0;
        var suggestions = new List<CompanyMatchSuggestion>();

        foreach (var vspcCompany in unmappedVspc)
        {
            var normalized = NameMatcher.Normalize(vspcCompany.Name);
            if (normalized.Length == 0) continue;

            if (atByNormalized.TryGetValue(normalized, out var exactMatches) && exactMatches.Count == 1)
            {
                var match = exactMatches[0];
                if (apply)
                {
                    _store.UpsertCompanyMapping(new CompanyMapping(
                        vspcCompany.InstanceUid, vspcCompany.Name, match.Id, match.CompanyName ?? "",
                        Auto: true, DateTimeOffset.UtcNow.ToString("O")));
                    atByNormalized.Remove(normalized);
                    autoMapped++;
                    _activity.Info("companies", $"Auto-mapped '{vspcCompany.Name}' to Autotask company '{match.CompanyName}' (#{match.Id})");
                    continue;
                }
                suggestions.Add(new CompanyMatchSuggestion(vspcCompany.InstanceUid, vspcCompany.Name,
                    new List<MatchCandidate> { new(match.Id, match.CompanyName ?? "", 1.0, true) }));
                continue;
            }

            var candidates = availableAt
                .Select(c => new MatchCandidate(c.Id, c.CompanyName ?? "",
                    NameMatcher.Similarity(vspcCompany.Name, c.CompanyName ?? ""), false))
                .Where(c => c.Score >= SuggestionThreshold)
                .OrderByDescending(c => c.Score)
                .Take(5)
                .ToList();
            if (candidates.Count > 0)
                suggestions.Add(new CompanyMatchSuggestion(vspcCompany.InstanceUid, vspcCompany.Name, candidates));
        }

        if (apply && autoMapped > 0)
            _activity.Info("companies", $"Auto-map complete: {autoMapped} companies mapped, {suggestions.Count} need review");
        return (autoMapped, suggestions);
    }

    public void Map(string vspcCompanyUid, long atCompanyId)
    {
        var snapshot = GetSnapshot();
        var vspcName = snapshot?.VspcCompanies.FirstOrDefault(c => c.InstanceUid == vspcCompanyUid)?.Name ?? vspcCompanyUid;
        var atName = snapshot?.AtCompanies.FirstOrDefault(c => c.Id == atCompanyId)?.CompanyName ?? atCompanyId.ToString();
        _store.UpsertCompanyMapping(new CompanyMapping(
            vspcCompanyUid, vspcName, atCompanyId, atName, Auto: false, DateTimeOffset.UtcNow.ToString("O")));
        _activity.Info("companies", $"Mapped '{vspcName}' to Autotask company '{atName}' (#{atCompanyId})");
    }

    public void Unmap(string vspcCompanyUid)
    {
        var existing = _store.GetCompanyMapping(vspcCompanyUid);
        _store.DeleteCompanyMapping(vspcCompanyUid);
        // Billing config depends on the mapping; remove it as well so syncs stop cleanly.
        _store.DeleteCompanyBilling(vspcCompanyUid);
        _activity.Info("companies", $"Removed mapping for '{existing?.VspcCompanyName ?? vspcCompanyUid}'");
    }

}
