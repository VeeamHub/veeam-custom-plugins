using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Storage;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Sync;

public sealed record BillingLineResult(
    string VspcCompanyUid,
    string VspcCompanyName,
    string ServiceKey,
    string ServiceName,
    long DesiredUnits,
    long CurrentUnits,
    long Delta,
    long? ContractId,
    long? ServiceId,
    string Status,   // adjusted | planned | no-change | skipped | error
    string? Detail);

/// <summary>
/// Billing synchronization — the Autotask counterpart of CWM's Billing feature.
/// VSPC usage counters → billable units per mapped service → ContractServiceAdjustments
/// so each company's recurring-service contract reflects measured consumption.
/// </summary>
public sealed class BillingSyncService
{
    public const string SettingsKey = "billing.settings";
    public const string LastRunKey = "billing.lastRun";

    private readonly VspcClient _vspc;
    private readonly AutotaskClient _autotask;
    private readonly Store _store;
    private readonly Db _db;
    private readonly ActivityLog _activity;

    public BillingSyncService(VspcClient vspc, AutotaskClient autotask, Store store, Db db, ActivityLog activity)
    {
        _vspc = vspc;
        _autotask = autotask;
        _store = store;
        _db = db;
        _activity = activity;
    }

    public BillingSettings GetSettings() => _db.GetJson<BillingSettings>(SettingsKey) ?? new BillingSettings();

    /// <summary>Runs the billing reconciliation. Dry runs compute and record deltas without writing to Autotask.</summary>
    public async Task<List<BillingLineResult>> RunAsync(bool dryRun, string? onlyCompanyUid = null, CancellationToken ct = default)
    {
        var settings = GetSettings();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (windowStart, windowEnd) = BillingPeriod.CurrentWindow(settings.AnchorDayOfMonth, today);
        var effectiveDate = DateTime.UtcNow.Date.ToString("yyyy-MM-ddT00:00:00") + "Z";

        var serviceMappings = _store.GetServiceMappings().ToDictionary(m => m.ServiceKey);
        var companies = _store.GetCompanyBillings();
        if (onlyCompanyUid != null)
            companies = companies.Where(c => c.VspcCompanyUid == onlyCompanyUid).ToList();

        var results = new List<BillingLineResult>();

        foreach (var companyBilling in companies)
        {
            ct.ThrowIfCancellationRequested();
            var mapping = _store.GetCompanyMapping(companyBilling.VspcCompanyUid);
            var companyName = mapping?.VspcCompanyName ?? companyBilling.VspcCompanyUid;

            if (mapping == null)
            {
                results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, "-", "-",
                    0, 0, 0, companyBilling.AtContractId, null, "error",
                    "Company is not mapped to an Autotask company; billing skipped"));
                continue;
            }

            List<UsageSample> samples;
            try
            {
                var usage = await _vspc.GetCompanyUsageAsync(companyBilling.VspcCompanyUid, windowStart, windowEnd, ct);
                samples = Flatten(usage);
            }
            catch (Exception ex) when (ex is VspcApiException or HttpRequestException)
            {
                results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, "-", "-",
                    0, 0, 0, companyBilling.AtContractId, null, "error",
                    $"Failed to read VSPC usage: {ex.Message}"));
                _activity.Error("billing", $"Usage read failed for '{companyName}'", ex.Message);
                continue;
            }

            List<AtContractService>? contractServices = null;

            foreach (var serviceKey in companyBilling.EnabledServices)
            {
                var service = ServiceCatalog.Find(serviceKey);
                if (service == null) continue;

                if (!serviceMappings.TryGetValue(serviceKey, out var serviceMapping) ||
                    serviceMapping.Mode == ServiceMapMode.Skip)
                {
                    results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, serviceKey,
                        service.DisplayName, 0, 0, 0, companyBilling.AtContractId, null, "skipped",
                        "Service is not mapped to an Autotask service"));
                    continue;
                }

                try
                {
                    // Resolve (or lazily create) the Autotask service this VSPC service bills to.
                    var serviceId = serviceMapping.AtServiceId;
                    if (serviceId == null)
                    {
                        if (serviceMapping.Mode != ServiceMapMode.CreateNew)
                            throw new AtApiException(400, "Service mapping has no Autotask service selected");
                        if (dryRun)
                        {
                            results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, serviceKey,
                                service.DisplayName, ServiceCatalog.ComputeUnits(service, samples), 0, 0,
                                companyBilling.AtContractId, null, "planned",
                                "Autotask service will be created on the first live run"));
                            continue;
                        }
                        serviceId = await CreateAutotaskServiceAsync(service, serviceMapping, settings, ct);
                        serviceMapping = serviceMapping with { AtServiceId = serviceId };
                        serviceMappings[serviceKey] = serviceMapping;
                        _store.UpsertServiceMapping(serviceMapping);
                    }

                    var desired = ServiceCatalog.ComputeUnits(service, samples);

                    contractServices ??= await _autotask.GetContractServicesAsync(companyBilling.AtContractId, ct);
                    var contractService = contractServices.FirstOrDefault(cs => cs.ServiceID == serviceId.Value);
                    var current = contractService == null
                        ? 0
                        : await _autotask.GetCurrentUnitsAsync(contractService.Id, DateTime.UtcNow.Date, ct);

                    var delta = desired - current;
                    if (delta == 0)
                    {
                        results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, serviceKey,
                            service.DisplayName, desired, current, 0, companyBilling.AtContractId, serviceId, "no-change", null));
                        continue;
                    }

                    if (dryRun)
                    {
                        results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, serviceKey,
                            service.DisplayName, desired, current, delta, companyBilling.AtContractId, serviceId,
                            "planned", $"Would post a ContractServiceAdjustment of {delta:+#;-#;0} units"));
                        _store.AddBillingAdjustment(companyBilling.VspcCompanyUid, companyName, serviceKey,
                            companyBilling.AtContractId, serviceId, current, desired, delta, effectiveDate,
                            dryRun: true, result: "planned");
                        continue;
                    }

                    // ContractServiceAdjustments are create-only unit deltas. Passing contractID +
                    // serviceID (when no ContractService link exists yet) makes Autotask create the link.
                    var payload = new Dictionary<string, object?>
                    {
                        ["unitChange"] = delta,
                        ["effectiveDate"] = effectiveDate
                    };
                    if (contractService != null)
                        payload["contractServiceID"] = contractService.Id;
                    else
                    {
                        payload["contractID"] = companyBilling.AtContractId;
                        payload["serviceID"] = serviceId.Value;
                    }

                    await _autotask.CreateAsync("ContractServiceAdjustments", payload, ct);
                    _store.AddBillingAdjustment(companyBilling.VspcCompanyUid, companyName, serviceKey,
                        companyBilling.AtContractId, serviceId, current, desired, delta, effectiveDate,
                        dryRun: false, result: "ok");
                    results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, serviceKey,
                        service.DisplayName, desired, current, delta, companyBilling.AtContractId, serviceId,
                        "adjusted", $"Units {current} → {desired}"));
                    _activity.Info("billing",
                        $"Adjusted '{service.DisplayName}' for '{companyName}': {current} → {desired} units " +
                        $"(contract #{companyBilling.AtContractId})");
                }
                catch (Exception ex) when (ex is AtApiException or VspcApiException or HttpRequestException)
                {
                    results.Add(new BillingLineResult(companyBilling.VspcCompanyUid, companyName, serviceKey,
                        service.DisplayName, 0, 0, 0, companyBilling.AtContractId, serviceMapping.AtServiceId,
                        "error", ex.Message));
                    _store.AddBillingAdjustment(companyBilling.VspcCompanyUid, companyName, serviceKey,
                        companyBilling.AtContractId, serviceMapping.AtServiceId, null, null, null, effectiveDate,
                        dryRun, "error: " + ex.Message);
                    _activity.Error("billing", $"Billing sync failed for '{companyName}' / {service.DisplayName}", ex.Message);
                }
            }
        }

        if (!dryRun)
            _db.SetSyncState(LastRunKey, DateTimeOffset.UtcNow.ToString("O"));

        var adjusted = results.Count(r => r.Status == "adjusted");
        var errors = results.Count(r => r.Status == "error");
        _activity.Info("billing",
            $"Billing {(dryRun ? "preview" : "sync")} finished for period {windowStart:yyyy-MM-dd}..{windowEnd:yyyy-MM-dd}: " +
            $"{adjusted} adjustments, {results.Count(r => r.Status == "no-change")} unchanged, {errors} errors");
        return results;
    }

    private async Task<long> CreateAutotaskServiceAsync(
        BillableService service, ServiceMapping mapping, BillingSettings settings, CancellationToken ct)
    {
        var billingCodeId = mapping.BillingCodeId ?? settings.DefaultBillingCodeId
            ?? throw new AtApiException(400,
                $"Cannot create Autotask service for '{service.DisplayName}': no billing code selected " +
                "(set one on the service mapping or a default in billing settings)");
        var periodType = mapping.PeriodType ?? settings.DefaultPeriodType
            ?? throw new AtApiException(400,
                $"Cannot create Autotask service for '{service.DisplayName}': no period type selected");

        var payload = new Dictionary<string, object?>
        {
            ["name"] = mapping.AtServiceName ?? $"Veeam — {service.DisplayName}",
            ["unitPrice"] = mapping.UnitPrice ?? 0,
            ["billingCodeID"] = billingCodeId,
            ["periodType"] = periodType,
            ["isActive"] = true
        };
        var id = await _autotask.CreateAsync("Services", payload, ct);
        _activity.Info("billing", $"Created Autotask service '{payload["name"]}' (#{id}) for {service.DisplayName}");
        return id;
    }

    private static List<UsageSample> Flatten(List<VspcCompanyUsage> usage)
    {
        var samples = new List<UsageSample>();
        foreach (var item in usage)
        {
            if (item.Counters == null || item.Date == null) continue;
            var date = DateOnly.FromDateTime(item.Date.Value.UtcDateTime);
            foreach (var counter in item.Counters)
            {
                if (counter.Type == null) continue;
                samples.Add(new UsageSample(date, counter.Type, counter.Value));
            }
        }
        return samples;
    }
}
