using System.Text.Json;
using Microsoft.Data.Sqlite;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;

namespace VspcAutotaskPlugin.Storage;

/// <summary>Typed CRUD over the plugin's SQLite tables.</summary>
public sealed class Store
{
    private readonly Db _db;
    public Store(Db db) => _db = db;

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    // ------------------------------------------------------ company mappings

    private static CompanyMapping MapCompanyMapping(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
        r.GetInt64(4) != 0, r.GetString(5));

    private const string CompanyMappingColumns =
        "vspc_company_uid, vspc_company_name, at_company_id, at_company_name, auto, mapped_at";

    public List<CompanyMapping> GetCompanyMappings() =>
        _db.Query($"SELECT {CompanyMappingColumns} FROM company_mappings", MapCompanyMapping);

    public CompanyMapping? GetCompanyMapping(string vspcCompanyUid) =>
        _db.Query($"SELECT {CompanyMappingColumns} FROM company_mappings WHERE vspc_company_uid = $uid",
            MapCompanyMapping, new() { ["$uid"] = vspcCompanyUid }).FirstOrDefault();

    public void UpsertCompanyMapping(CompanyMapping m) =>
        _db.Exec("""
            INSERT INTO company_mappings (vspc_company_uid, vspc_company_name, at_company_id, at_company_name, auto, mapped_at)
            VALUES ($uid, $vname, $atid, $atname, $auto, $at)
            ON CONFLICT(vspc_company_uid) DO UPDATE SET
                vspc_company_name = $vname, at_company_id = $atid, at_company_name = $atname, auto = $auto, mapped_at = $at
            """,
            new()
            {
                ["$uid"] = m.VspcCompanyUid, ["$vname"] = m.VspcCompanyName,
                ["$atid"] = m.AtCompanyId, ["$atname"] = m.AtCompanyName,
                ["$auto"] = m.Auto ? 1 : 0, ["$at"] = m.MappedAt
            });

    public void DeleteCompanyMapping(string vspcCompanyUid) =>
        _db.Exec("DELETE FROM company_mappings WHERE vspc_company_uid = $uid",
            new() { ["$uid"] = vspcCompanyUid });

    // ------------------------------------------------------ service mappings

    private static ServiceMapping MapServiceMapping(SqliteDataReader r) => new(
        r.GetString(0),
        Enum.TryParse<ServiceMapMode>(r.GetString(1), out var mode) ? mode : ServiceMapMode.Skip,
        r.IsDBNull(2) ? null : r.GetInt64(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetDouble(4),
        r.IsDBNull(5) ? null : r.GetInt64(5),
        r.IsDBNull(6) ? null : r.GetInt32(6),
        r.GetString(7));

    private const string ServiceMappingColumns =
        "service_key, mode, at_service_id, at_service_name, unit_price, billing_code_id, period_type, updated_at";

    public List<ServiceMapping> GetServiceMappings() =>
        _db.Query($"SELECT {ServiceMappingColumns} FROM service_mappings", MapServiceMapping);

    public ServiceMapping? GetServiceMapping(string serviceKey) =>
        _db.Query($"SELECT {ServiceMappingColumns} FROM service_mappings WHERE service_key = $key",
            MapServiceMapping, new() { ["$key"] = serviceKey }).FirstOrDefault();

    public void UpsertServiceMapping(ServiceMapping m) =>
        _db.Exec("""
            INSERT INTO service_mappings (service_key, mode, at_service_id, at_service_name, unit_price, billing_code_id, period_type, updated_at)
            VALUES ($key, $mode, $sid, $sname, $price, $bcode, $ptype, $at)
            ON CONFLICT(service_key) DO UPDATE SET
                mode = $mode, at_service_id = $sid, at_service_name = $sname,
                unit_price = $price, billing_code_id = $bcode, period_type = $ptype, updated_at = $at
            """,
            new()
            {
                ["$key"] = m.ServiceKey, ["$mode"] = m.Mode.ToString(),
                ["$sid"] = m.AtServiceId, ["$sname"] = m.AtServiceName,
                ["$price"] = m.UnitPrice, ["$bcode"] = m.BillingCodeId,
                ["$ptype"] = m.PeriodType, ["$at"] = Now()
            });

    // ------------------------------------------------------- company billing

    private static CompanyBilling MapCompanyBilling(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new List<string>(),
        r.GetString(4));

    private const string CompanyBillingColumns =
        "vspc_company_uid, at_contract_id, at_contract_name, enabled_services, updated_at";

    public List<CompanyBilling> GetCompanyBillings() =>
        _db.Query($"SELECT {CompanyBillingColumns} FROM company_billing", MapCompanyBilling);

    public CompanyBilling? GetCompanyBilling(string vspcCompanyUid) =>
        _db.Query($"SELECT {CompanyBillingColumns} FROM company_billing WHERE vspc_company_uid = $uid",
            MapCompanyBilling, new() { ["$uid"] = vspcCompanyUid }).FirstOrDefault();

    public void UpsertCompanyBilling(CompanyBilling b) =>
        _db.Exec("""
            INSERT INTO company_billing (vspc_company_uid, at_contract_id, at_contract_name, enabled_services, updated_at)
            VALUES ($uid, $cid, $cname, $services, $at)
            ON CONFLICT(vspc_company_uid) DO UPDATE SET
                at_contract_id = $cid, at_contract_name = $cname, enabled_services = $services, updated_at = $at
            """,
            new()
            {
                ["$uid"] = b.VspcCompanyUid, ["$cid"] = b.AtContractId, ["$cname"] = b.AtContractName,
                ["$services"] = JsonSerializer.Serialize(b.EnabledServices), ["$at"] = Now()
            });

    public void DeleteCompanyBilling(string vspcCompanyUid) =>
        _db.Exec("DELETE FROM company_billing WHERE vspc_company_uid = $uid",
            new() { ["$uid"] = vspcCompanyUid });

    // ----------------------------------------------------------- alarm rules

    public List<AlarmRule> GetAlarmRules() =>
        _db.Query("SELECT alarm_template_uid, enabled, name, category, internal_id FROM alarm_rules",
            r => new AlarmRule(
                r.GetString(0), r.GetInt64(1) != 0,
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4)));

    public HashSet<string> GetEnabledAlarmUids() =>
        _db.Query("SELECT alarm_template_uid FROM alarm_rules WHERE enabled = 1", r => r.GetString(0))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Refreshes the known-alarm list from VSPC templates, preserving enabled flags.</summary>
    public void SyncAlarmRules(IEnumerable<AlarmRule> templates)
    {
        foreach (var t in templates)
        {
            _db.Exec("""
                INSERT INTO alarm_rules (alarm_template_uid, enabled, name, category, internal_id, updated_at)
                VALUES ($uid, $enabled, $name, $cat, $iid, $at)
                ON CONFLICT(alarm_template_uid) DO UPDATE SET
                    name = $name, category = $cat, internal_id = $iid, updated_at = $at
                """,
                new()
                {
                    ["$uid"] = t.AlarmTemplateUid, ["$enabled"] = t.Enabled ? 1 : 0,
                    ["$name"] = t.Name, ["$cat"] = t.Category, ["$iid"] = t.InternalId, ["$at"] = Now()
                });
        }
    }

    public void SetAlarmRulesEnabled(IEnumerable<string> alarmTemplateUids, bool enabled)
    {
        foreach (var uid in alarmTemplateUids)
            _db.Exec("UPDATE alarm_rules SET enabled = $enabled, updated_at = $at WHERE alarm_template_uid = $uid",
                new() { ["$enabled"] = enabled ? 1 : 0, ["$at"] = Now(), ["$uid"] = uid });
    }

    // ---------------------------------------------------------- ticket links

    private static TicketLink MapTicketLink(SqliteDataReader r) => new()
    {
        ActiveAlarmUid = r.GetString(0),
        VspcCompanyUid = r.IsDBNull(1) ? null : r.GetString(1),
        VspcCompanyName = r.IsDBNull(2) ? null : r.GetString(2),
        AlarmTemplateUid = r.IsDBNull(3) ? null : r.GetString(3),
        AlarmName = r.IsDBNull(4) ? null : r.GetString(4),
        ObjectName = r.IsDBNull(5) ? null : r.GetString(5),
        AtCompanyId = r.IsDBNull(6) ? null : r.GetInt64(6),
        AtTicketId = r.IsDBNull(7) ? null : r.GetInt64(7),
        AtTicketNumber = r.IsDBNull(8) ? null : r.GetString(8),
        State = Enum.TryParse<TicketLinkState>(r.GetString(9), out var st) ? st : TicketLinkState.Error,
        LastStatus = r.IsDBNull(10) ? null : r.GetString(10),
        LastActivationTime = r.IsDBNull(11) ? null : r.GetString(11),
        RepeatCount = r.GetInt32(12),
        DueAt = r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13)),
        LastError = r.IsDBNull(14) ? null : r.GetString(14),
        CreatedAt = r.GetString(15),
        UpdatedAt = r.GetString(16)
    };

    private const string TicketLinkColumns = """
        active_alarm_uid, vspc_company_uid, vspc_company_name, alarm_template_uid, alarm_name,
        object_name, at_company_id, at_ticket_id, at_ticket_number, state, last_status,
        last_activation_time, repeat_count, due_at, last_error, created_at, updated_at
        """;

    public TicketLink? GetTicketLink(string activeAlarmUid) =>
        _db.Query($"SELECT {TicketLinkColumns} FROM ticket_links WHERE active_alarm_uid = $uid",
            MapTicketLink, new() { ["$uid"] = activeAlarmUid }).FirstOrDefault();

    public List<TicketLink> GetTicketLinksByState(params TicketLinkState[] states)
    {
        var placeholders = string.Join(",", states.Select((_, i) => "$s" + i));
        var args = new Dictionary<string, object?>();
        for (var i = 0; i < states.Length; i++) args["$s" + i] = states[i].ToString();
        return _db.Query($"SELECT {TicketLinkColumns} FROM ticket_links WHERE state IN ({placeholders})",
            MapTicketLink, args);
    }

    public List<TicketLink> GetRecentTicketLinks(int limit = 200) =>
        _db.Query($"SELECT {TicketLinkColumns} FROM ticket_links ORDER BY updated_at DESC LIMIT {Math.Clamp(limit, 1, 1000)}",
            MapTicketLink);

    public void UpsertTicketLink(TicketLink link)
    {
        link.UpdatedAt = Now();
        if (string.IsNullOrEmpty(link.CreatedAt)) link.CreatedAt = link.UpdatedAt;
        _db.Exec("""
            INSERT INTO ticket_links (active_alarm_uid, vspc_company_uid, vspc_company_name, alarm_template_uid,
                alarm_name, object_name, at_company_id, at_ticket_id, at_ticket_number, state, last_status,
                last_activation_time, repeat_count, due_at, last_error, created_at, updated_at)
            VALUES ($uid, $cuid, $cname, $tuid, $aname, $oname, $atcid, $tid, $tnum, $state, $status,
                $acttime, $repeat, $due, $err, $created, $updated)
            ON CONFLICT(active_alarm_uid) DO UPDATE SET
                vspc_company_uid = $cuid, vspc_company_name = $cname, alarm_template_uid = $tuid,
                alarm_name = $aname, object_name = $oname, at_company_id = $atcid, at_ticket_id = $tid,
                at_ticket_number = $tnum, state = $state, last_status = $status, last_activation_time = $acttime,
                repeat_count = $repeat, due_at = $due, last_error = $err, updated_at = $updated
            """,
            new()
            {
                ["$uid"] = link.ActiveAlarmUid, ["$cuid"] = link.VspcCompanyUid, ["$cname"] = link.VspcCompanyName,
                ["$tuid"] = link.AlarmTemplateUid, ["$aname"] = link.AlarmName, ["$oname"] = link.ObjectName,
                ["$atcid"] = link.AtCompanyId, ["$tid"] = link.AtTicketId, ["$tnum"] = link.AtTicketNumber,
                ["$state"] = link.State.ToString(), ["$status"] = link.LastStatus,
                ["$acttime"] = link.LastActivationTime, ["$repeat"] = link.RepeatCount,
                ["$due"] = link.DueAt?.ToString("O"), ["$err"] = link.LastError,
                ["$created"] = link.CreatedAt, ["$updated"] = link.UpdatedAt
            });
    }

    // ---------------------------------------------------- billing adjustments

    public void AddBillingAdjustment(
        string vspcCompanyUid, string? vspcCompanyName, string serviceKey,
        long? contractId, long? serviceId, long? prevUnits, long? newUnits, long? delta,
        string? effectiveDate, bool dryRun, string? result) =>
        _db.Exec("""
            INSERT INTO billing_adjustments (ts, vspc_company_uid, vspc_company_name, service_key,
                at_contract_id, at_service_id, prev_units, new_units, delta, effective_date, dry_run, result)
            VALUES ($ts, $uid, $name, $key, $cid, $sid, $prev, $new, $delta, $eff, $dry, $result)
            """,
            new()
            {
                ["$ts"] = Now(), ["$uid"] = vspcCompanyUid, ["$name"] = vspcCompanyName, ["$key"] = serviceKey,
                ["$cid"] = contractId, ["$sid"] = serviceId, ["$prev"] = prevUnits, ["$new"] = newUnits,
                ["$delta"] = delta, ["$eff"] = effectiveDate, ["$dry"] = dryRun ? 1 : 0, ["$result"] = result
            });

    public List<BillingAdjustmentEntry> GetBillingHistory(int limit = 300) =>
        _db.Query($"""
            SELECT id, ts, vspc_company_uid, vspc_company_name, service_key, at_contract_id, at_service_id,
                   prev_units, new_units, delta, effective_date, dry_run, result
            FROM billing_adjustments ORDER BY id DESC LIMIT {Math.Clamp(limit, 1, 2000)}
            """,
            r => new BillingAdjustmentEntry(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.GetInt64(11) != 0,
                r.IsDBNull(12) ? null : r.GetString(12)));
}
