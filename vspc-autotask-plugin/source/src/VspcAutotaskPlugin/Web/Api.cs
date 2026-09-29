using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Plugin;
using VspcAutotaskPlugin.Storage;
using VspcAutotaskPlugin.Sync;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Web;

// ---- request bodies --------------------------------------------------------

public sealed record AutotaskSettingsRequest(string Username, string? Secret, string IntegrationCode);
public sealed record FeaturesRequest(bool Companies, bool Billing, bool Ticketing);
public sealed record AutomapRequest(bool Apply);
public sealed record MapRequest(string VspcUid, long AtId);
public sealed record UnmapRequest(string VspcUid);
public sealed record ServiceMappingRequest(string ServiceKey, string Mode, long? AtServiceId, string? AtServiceName, double? UnitPrice, long? BillingCodeId, int? PeriodType);
public sealed record CompanyBillingRequest(string VspcUid, long ContractId, string? ContractName, List<string> EnabledServices);
public sealed record RemoveCompanyBillingRequest(string VspcUid);
public sealed record AlarmEnableRequest(List<string> Uids, bool Enabled);
public sealed record BillingRunRequest(string? CompanyUid);

public static class Api
{
    /// <summary>Alarm categories excluded from ticketing, mirroring the CWM plugin's exclusions
    /// (Cloud Gateway, Internal, Plugin, Site and User objects).</summary>
    private static readonly HashSet<string> ExcludedAlarmCategories = new(StringComparer.OrdinalIgnoreCase)
    { "BackupCloudGateway", "Internal", "Integrator", "Site", "User" };

    private static readonly string[] PublicApiPaths =
    { "/api/session", "/api/health", "/api/v1/healthcheck", "/api/v1/readiness" };

    public static void MapApi(WebApplication app)
    {
        // ---- auth gate for everything else under /api -----------------------
        // /api/v1/* are host-management endpoints the VSPC service calls directly on the
        // loopback plugin port; everything else requires a VSPC-authenticated portal user
        // with an administrative role (X-User-Uid header added by the VSPC UI proxy).
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
                !PublicApiPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                !path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
            {
                var pluginAuth = ctx.RequestServices.GetRequiredService<PluginAuthService>();
                if (!await pluginAuth.IsAuthorizedAsync(ctx))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Not authenticated" });
                    return;
                }
            }
            await next();
        });

        app.MapGet("/api/health", () => Results.Ok(new { ok = true, name = "vspc-autotask-plugin" }));

        MapSession(app);
        MapStatus(app);
        MapSettings(app);
        MapAutotaskMetadata(app);
        MapCompanies(app);
        MapBilling(app);
        MapTicketing(app);
        MapActivity(app);
    }

    // ------------------------------------------------------------- session

    private static void MapSession(WebApplication app)
    {
        // VSPC authenticates portal users; this endpoint reports whether the current
        // user (X-User-Uid) holds a role permitted to administer the plugin. Logged in
        // detail because it runs exactly once per page load and pinpoints auth issues.
        app.MapGet("/api/session", async (HttpContext ctx, PluginAuthService pluginAuth, ILoggerFactory loggerFactory) =>
        {
            var uid = ctx.Request.Headers[PluginAuthService.UserUidHeader].ToString();
            var hasBearer =
                !string.IsNullOrEmpty(ctx.Request.Headers[PluginAuthService.AccessTokenHeader].ToString()) ||
                ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            var authenticated = await pluginAuth.IsAuthorizedAsync(ctx);
            var log = loggerFactory.CreateLogger("Session");
            log.LogInformation(
                "Session check: X-User-Uid {Uid}, bearer {Bearer} -> authenticated={Authenticated}",
                string.IsNullOrEmpty(uid) ? "ABSENT" : "present (" + uid + ")",
                hasBearer ? "present" : "ABSENT", authenticated);
            if (!authenticated)
                log.LogInformation("Unauthenticated session request carried headers: {Headers}",
                    string.Join(", ", ctx.Request.Headers.Select(h => h.Key).OrderBy(k => k)));
            return Results.Ok(new { authenticated });
        });
    }

    // -------------------------------------------------------------- status

    private static void MapStatus(WebApplication app)
    {
        app.MapGet("/api/status", async (Db db, Store store, VspcClient vspc, AutotaskClient autotask,
            CompanySyncService companies, TicketSyncService tickets, CancellationToken ct) =>
        {
            var vspcSettings = vspc.GetSettings();
            var atSettings = autotask.GetSettings();
            var features = db.GetJson<FeatureToggles>("features") ?? new FeatureToggles();
            var snapshot = companies.GetSnapshot();
            var openTickets = store.GetTicketLinksByState(TicketLinkState.Open).Count;
            var pendingTickets = store.GetTicketLinksByState(TicketLinkState.Pending).Count;
            // Live (cached) reachability check: the Dashboard tile only claims "Configured"
            // when Autotask actually answers with the stored credentials.
            var atCheck = await autotask.CheckConnectionCachedAsync(ct);

            return Results.Ok(new
            {
                vspc = new { configured = vspc.IsConfigured, baseUrl = vspcSettings?.BaseUrl },
                autotask = new
                {
                    configured = autotask.IsConfigured,
                    connected = atCheck.Ok,
                    error = atCheck.Error,
                    zoneUrl = atSettings?.ZoneUrl
                },
                features,
                counts = new
                {
                    vspcCompanies = snapshot?.VspcCompanies.Count ?? 0,
                    atCompanies = snapshot?.AtCompanies.Count ?? 0,
                    mappedCompanies = store.GetCompanyMappings().Count,
                    billingCompanies = store.GetCompanyBillings().Count,
                    enabledAlarms = store.GetEnabledAlarmUids().Count,
                    openTickets,
                    pendingTickets
                },
                lastTicketPoll = tickets.GetLastSummary(),
                lastBillingRun = db.GetSyncState(BillingSyncService.LastRunKey),
                snapshotAge = snapshot?.FetchedAt
            });
        });
    }

    // ------------------------------------------------------------ settings

    private static void MapSettings(WebApplication app)
    {
        // The VSPC connection is provisioned by the plugin host (gRPC getApiKey/discoverService);
        // only the Autotask connection and behavior settings are user-configurable.

        app.MapGet("/api/settings/autotask", (AutotaskClient autotask) =>
        {
            var s = autotask.GetSettings();
            return Results.Ok(new
            {
                username = s?.Username ?? "",
                integrationCode = s?.IntegrationCode ?? "",
                hasSecret = !string.IsNullOrEmpty(s?.SecretEnc),
                zoneUrl = s?.ZoneUrl ?? ""
            });
        });

        app.MapPost("/api/settings/autotask", async (AutotaskSettingsRequest body, Db db,
            SecretProtector protector, AutotaskClient autotask, ActivityLog activity) =>
        {
            var existing = autotask.GetSettings();
            var candidate = new AutotaskConnectionSettings
            {
                Username = body.Username.Trim(),
                IntegrationCode = body.IntegrationCode.Trim(),
                SecretEnc = string.IsNullOrEmpty(body.Secret) ? existing?.SecretEnc : protector.Protect(body.Secret),
                ZoneUrl = string.Equals(existing?.Username, body.Username.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? existing?.ZoneUrl : null // re-resolve the zone when the API user changes
            };
            var (ok, message, updated) = await autotask.TestConnectionAsync(candidate);
            if (!ok) return Results.BadRequest(new { ok, message });
            db.SetJson(AutotaskClient.SettingsKey, updated);
            autotask.InvalidateConnectionCheck();
            activity.Info("connection", "Autotask connection settings updated", message);
            return Results.Ok(new { ok, message });
        });

        // CWM parity: "Change > Disconnect" on the plugin connection dialog.
        app.MapPost("/api/settings/autotask/disconnect", (Db db, AutotaskClient autotask, ActivityLog activity) =>
        {
            db.DeleteSetting(AutotaskClient.SettingsKey);
            autotask.InvalidateConnectionCheck();
            activity.Info("connection", "Autotask connection removed (disconnected from the configuration dialog)");
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/settings/features", (Db db) =>
            Results.Ok(db.GetJson<FeatureToggles>("features") ?? new FeatureToggles()));

        app.MapPost("/api/settings/features", (FeaturesRequest body, Db db, TicketSyncService tickets, ActivityLog activity) =>
        {
            var previous = db.GetJson<FeatureToggles>("features") ?? new FeatureToggles();
            var next = new FeatureToggles { Companies = body.Companies, Billing = body.Billing, Ticketing = body.Ticketing };
            db.SetJson("features", next);
            if (next.Ticketing && !previous.Ticketing)
            {
                // Record the enablement moment: alarms triggered before it never become tickets.
                var settings = tickets.GetSettings();
                if (settings.EnabledAt == null)
                {
                    settings.EnabledAt = DateTimeOffset.UtcNow;
                    db.SetJson(TicketSyncService.SettingsKey, settings);
                }
            }
            activity.Info("system",
                $"Features updated — companies: {next.Companies}, billing: {next.Billing}, ticketing: {next.Ticketing}");
            return Results.Ok(next);
        });

        app.MapGet("/api/settings/ticketing", (TicketSyncService tickets) => Results.Ok(tickets.GetSettings()));

        app.MapPost("/api/settings/ticketing", (TicketingSettings body, Db db, TicketSyncService tickets, ActivityLog activity) =>
        {
            // New and Complete statuses must differ, or bidirectional close detection treats every
            // freshly-created ticket as already closed (false alarm resolution + duplicate tickets).
            if (body.NewStatusId != null && body.NewStatusId == body.CompleteStatusId)
                return Results.BadRequest(new
                {
                    error = "The 'new ticket status' and 'closed ticket status' must be different Autotask statuses."
                });

            var existing = tickets.GetSettings();
            body.EnabledAt = existing.EnabledAt; // not client-editable
            body.DelayMinutes = Math.Clamp(body.DelayMinutes, 0, 24 * 60);
            body.DueHours = Math.Clamp(body.DueHours, 1, 24 * 30);
            body.PollSeconds = Math.Clamp(body.PollSeconds, 30, 3600);
            db.SetJson(TicketSyncService.SettingsKey, body);
            activity.Info("ticketing", "Ticketing settings updated");
            return Results.Ok(body);
        });

        app.MapGet("/api/settings/billing", (BillingSyncService billing) => Results.Ok(billing.GetSettings()));

        app.MapPost("/api/settings/billing", (BillingSettings body, Db db, ActivityLog activity) =>
        {
            body.AnchorDayOfMonth = Math.Clamp(body.AnchorDayOfMonth, 1, 28);
            body.SyncHourUtc = Math.Clamp(body.SyncHourUtc, 0, 23);
            db.SetJson(BillingSyncService.SettingsKey, body);
            activity.Info("billing", "Billing settings updated");
            return Results.Ok(body);
        });
    }

    // --------------------------------------------------- autotask metadata

    private static void MapAutotaskMetadata(WebApplication app)
    {
        app.MapGet("/api/autotask/picklists", async (AutotaskClient autotask) =>
        {
            async Task<object> Pick(string field) =>
                (await autotask.GetPicklistAsync("Tickets", field))
                .Select(v => new { value = v.Value, label = v.Label, isDefault = v.IsDefaultValue }).ToList();
            return Results.Ok(new
            {
                queues = await Pick("queueID"),
                statuses = await Pick("status"),
                priorities = await Pick("priority"),
                sources = await Pick("source"),
                ticketTypes = await Pick("ticketType")
            });
        });

        // Company type labels (instance-configurable picklist) for the Companies page Type column.
        app.MapGet("/api/autotask/companytypes", async (AutotaskClient autotask) =>
            Results.Ok((await autotask.GetPicklistAsync("Companies", "companyType"))
                .Select(v => new { value = v.Value, label = v.Label })));

        app.MapGet("/api/autotask/servicefields", async (AutotaskClient autotask) => Results.Ok(new
        {
            periodTypes = (await autotask.GetPicklistAsync("Services", "periodType"))
                .Select(v => new { value = v.Value, label = v.Label }).ToList()
        }));

        app.MapGet("/api/autotask/services", async (AutotaskClient autotask) =>
            Results.Ok((await autotask.GetActiveServicesAsync())
                .OrderBy(s => s.Name)
                .Select(s => new { id = s.Id, name = s.Name, unitPrice = s.UnitPrice })));

        app.MapGet("/api/autotask/billingcodes", async (AutotaskClient autotask) =>
            Results.Ok((await autotask.GetBillingCodesAsync())
                .OrderBy(c => c.Name)
                .Select(c => new { id = c.Id, name = c.Name })));

        app.MapGet("/api/autotask/contracts", async (long companyId, AutotaskClient autotask,
            ILoggerFactory loggerFactory) =>
        {
            // companyId 0 is legitimate: Autotask reserves it for the account's own company
            // record and returns that company's real contracts. Only a negative id is invalid.
            if (companyId < 0)
            {
                loggerFactory.CreateLogger("Api").LogWarning(
                    "Contract lookup requested with invalid companyId {CompanyId} — returning empty", companyId);
                return Results.Ok(Array.Empty<object>());
            }
            return Results.Ok((await autotask.GetCompanyContractsAsync(companyId))
                .OrderBy(c => c.ContractName)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.ContractName,
                    contractType = c.ContractType,
                    status = c.Status,
                    endDate = c.EndDate
                }));
        });
    }

    // ----------------------------------------------------------- companies

    private static void MapCompanies(WebApplication app)
    {
        app.MapGet("/api/companies", (CompanySyncService companies, Store store) =>
        {
            var snapshot = companies.GetSnapshot();
            if (snapshot == null)
                return Results.Ok(new { needsRefresh = true });

            var mappings = store.GetCompanyMappings().ToDictionary(m => m.VspcCompanyUid, StringComparer.OrdinalIgnoreCase);
            var mappedAtIds = mappings.Values.Select(m => m.AtCompanyId).ToHashSet();
            // Autotask company type (picklist value) comes from the cached Autotask list, so the
            // mapping rows can show it without a per-row lookup.
            var atTypeById = snapshot.AtCompanies
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.First().CompanyType);
            return Results.Ok(new
            {
                needsRefresh = false,
                fetchedAt = snapshot.FetchedAt,
                vspcCompanies = snapshot.VspcCompanies.Select(c => new
                {
                    uid = c.InstanceUid,
                    name = c.Name,
                    status = c.Status,
                    mapping = mappings.TryGetValue(c.InstanceUid, out var m)
                        ? new
                        {
                            atId = m.AtCompanyId,
                            atName = m.AtCompanyName,
                            auto = m.Auto,
                            atType = atTypeById.TryGetValue(m.AtCompanyId, out var t) ? t : null
                        }
                        : null
                }),
                atCompanies = snapshot.AtCompanies.Select(c => new
                {
                    id = c.Id,
                    name = c.CompanyName,
                    mapped = mappedAtIds.Contains(c.Id)
                })
            });
        });

        app.MapPost("/api/companies/refresh", async (CompanySyncService companies) =>
        {
            var snapshot = await companies.RefreshAsync();
            return Results.Ok(new { ok = true, vspc = snapshot.VspcCompanies.Count, autotask = snapshot.AtCompanies.Count });
        });

        app.MapPost("/api/companies/automap", async (AutomapRequest body, CompanySyncService companies) =>
        {
            var snapshot = companies.GetSnapshot() ?? await companies.RefreshAsync();
            var (autoMapped, suggestions) = companies.AutoMap(snapshot, body.Apply);
            return Results.Ok(new { autoMapped, suggestions });
        });

        app.MapPost("/api/companies/map", (MapRequest body, CompanySyncService companies) =>
        {
            companies.Map(body.VspcUid, body.AtId);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/companies/unmap", (UnmapRequest body, CompanySyncService companies) =>
        {
            companies.Unmap(body.VspcUid);
            return Results.Ok(new { ok = true });
        });

    }

    // ------------------------------------------------------------- billing

    private static void MapBilling(WebApplication app)
    {
        app.MapGet("/api/billing/services", (Store store) =>
        {
            var mappings = store.GetServiceMappings().ToDictionary(m => m.ServiceKey);
            return Results.Ok(ServiceCatalog.All.Select(s =>
            {
                mappings.TryGetValue(s.Key, out var m);
                return new
                {
                    key = s.Key,
                    group = s.Group,
                    name = s.DisplayName,
                    unit = s.Unit,
                    aggregation = s.Aggregation.ToString(),
                    mode = m?.Mode.ToString() ?? "Skip",
                    atServiceId = m?.AtServiceId,
                    atServiceName = m?.AtServiceName,
                    unitPrice = m?.UnitPrice,
                    billingCodeId = m?.BillingCodeId,
                    periodType = m?.PeriodType
                };
            }));
        });

        app.MapPost("/api/billing/services", (ServiceMappingRequest body, Store store) =>
        {
            if (ServiceCatalog.Find(body.ServiceKey) == null)
                return Results.BadRequest(new { error = $"Unknown service key '{body.ServiceKey}'" });
            var mode = Enum.TryParse<ServiceMapMode>(body.Mode, true, out var parsed) ? parsed : ServiceMapMode.Skip;
            store.UpsertServiceMapping(new ServiceMapping(
                body.ServiceKey, mode,
                mode == ServiceMapMode.Existing ? body.AtServiceId : mode == ServiceMapMode.CreateNew ? body.AtServiceId : null,
                body.AtServiceName, body.UnitPrice, body.BillingCodeId, body.PeriodType,
                DateTimeOffset.UtcNow.ToString("O")));
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/billing/companies", (Store store) =>
        {
            var billings = store.GetCompanyBillings().ToDictionary(b => b.VspcCompanyUid, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(store.GetCompanyMappings().Select(m => new
            {
                vspcUid = m.VspcCompanyUid,
                name = m.VspcCompanyName,
                atCompanyId = m.AtCompanyId,
                atCompanyName = m.AtCompanyName,
                contractId = billings.TryGetValue(m.VspcCompanyUid, out var b) ? (long?)b.AtContractId : null,
                contractName = billings.TryGetValue(m.VspcCompanyUid, out var b2) ? b2.AtContractName : null,
                enabledServices = billings.TryGetValue(m.VspcCompanyUid, out var b3) ? b3.EnabledServices : new List<string>()
            }));
        });

        app.MapPost("/api/billing/companies", (CompanyBillingRequest body, Store store, ActivityLog activity) =>
        {
            if (store.GetCompanyMapping(body.VspcUid) == null)
                return Results.BadRequest(new { error = "Company must be mapped before billing can be configured" });
            var valid = body.EnabledServices.Where(k => ServiceCatalog.Find(k) != null).Distinct().ToList();
            store.UpsertCompanyBilling(new CompanyBilling(
                body.VspcUid, body.ContractId, body.ContractName, valid, DateTimeOffset.UtcNow.ToString("O")));
            activity.Info("billing",
                $"Billing configured for company mapping '{body.VspcUid}': contract #{body.ContractId}, {valid.Count} services");
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/billing/companies/remove", (RemoveCompanyBillingRequest body, Store store) =>
        {
            store.DeleteCompanyBilling(body.VspcUid);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/billing/preview", async (BillingRunRequest body, BillingSyncService billing) =>
            Results.Ok(await billing.RunAsync(dryRun: true, body.CompanyUid)));

        app.MapPost("/api/billing/run", async (BillingRunRequest body, BillingSyncService billing) =>
            Results.Ok(await billing.RunAsync(dryRun: false, body.CompanyUid)));

        app.MapGet("/api/billing/history", (Store store, int? limit) =>
            Results.Ok(store.GetBillingHistory(limit ?? 300)));

        // VSPC subscription plans price the services created in Autotask (Product Mapping tab).
        app.MapGet("/api/vspc/subscriptionplans", async (VspcClient vspc, CancellationToken ct) =>
        {
            try
            {
                var plans = await vspc.GetSubscriptionPlansAsync(ct);
                return Results.Ok(plans
                    .OrderBy(p => p.Name)
                    .Select(p => new { uid = p.InstanceUid, name = p.Name, currency = p.Currency }));
            }
            catch (Exception ex) when (ex is VspcApiException or HttpRequestException)
            {
                // Non-fatal: the picker simply stays empty if VSPC declines the request.
                return Results.Ok(Array.Empty<object>());
            }
        });
    }

    // ----------------------------------------------------------- ticketing

    private static void MapTicketing(WebApplication app)
    {
        app.MapGet("/api/ticketing/alarms", (Store store) =>
        {
            var rules = store.GetAlarmRules()
                .Where(r => r.Category == null || !ExcludedAlarmCategories.Contains(r.Category))
                .OrderBy(r => r.Category).ThenBy(r => r.Name)
                .Select(r => new
                {
                    uid = r.AlarmTemplateUid,
                    name = r.Name,
                    category = r.Category,
                    internalId = r.InternalId,
                    enabled = r.Enabled
                });
            return Results.Ok(rules);
        });

        app.MapPost("/api/ticketing/alarms/refresh", async (VspcClient vspc, Store store) =>
        {
            var templates = await vspc.GetAlarmTemplatesAsync();
            store.SyncAlarmRules(templates.Select(t => new AlarmRule(
                t.InstanceUid, false, t.Name, t.Category, t.InternalId)));
            return Results.Ok(new { ok = true, count = templates.Count });
        });

        app.MapPost("/api/ticketing/alarms/enable", (AlarmEnableRequest body, Store store, ActivityLog activity) =>
        {
            store.SetAlarmRulesEnabled(body.Uids, body.Enabled);
            activity.Info("ticketing", $"{(body.Enabled ? "Enabled" : "Disabled")} ticket creation for {body.Uids.Count} alarm(s)");
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/ticketing/links", (Store store, int? limit) =>
            Results.Ok(store.GetRecentTicketLinks(limit ?? 200).Select(l => new
            {
                activeAlarmUid = l.ActiveAlarmUid,
                company = l.VspcCompanyName,
                alarm = l.AlarmName,
                objectName = l.ObjectName,
                ticketId = l.AtTicketId,
                ticketNumber = l.AtTicketNumber,
                state = l.State.ToString(),
                lastStatus = l.LastStatus,
                repeatCount = l.RepeatCount,
                dueAt = l.DueAt,
                error = l.LastError,
                updatedAt = l.UpdatedAt
            })));

        app.MapPost("/api/ticketing/poll", async (TicketSyncService tickets) =>
            Results.Ok(await tickets.PollOnceAsync()));
    }

    // ------------------------------------------------------------ activity

    private static void MapActivity(WebApplication app)
    {
        app.MapGet("/api/activity", (ActivityLog activity, int? limit, string? area, string? level) =>
            Results.Ok(activity.Query(limit ?? 200, area, level)));
    }
}
