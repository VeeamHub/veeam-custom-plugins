namespace VspcAutotaskPlugin.Domain;

public enum UsageAggregation
{
    /// <summary>Point-in-time counter: bill the most recent reported value (CWM: "actual number as of the invoice date").</summary>
    LastDay,
    /// <summary>Consumption counter: bill the sum over the billing period (CWM: "resources consumed between two successive invoices").</summary>
    SumOverPeriod
}

public enum UnitConversion { Count, BytesToGb }

/// <summary>A single flattened usage data point from the VSPC usage API.</summary>
public sealed record UsageSample(DateOnly Date, string CounterType, long Value);

public sealed record BillableService(
    string Key,
    string Group,
    string DisplayName,
    string Unit,
    string[] CounterTypes,
    UsageAggregation Aggregation,
    UnitConversion Conversion);

/// <summary>
/// The catalog of VSPC services that can be mapped to Autotask contract services —
/// the equivalent of the ConnectWise Manage plugin's "Product Mapping" service list.
/// Counter type names come from the VSPC 9.2 REST API usage counter enum.
/// </summary>
public static class ServiceCatalog
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    public static readonly IReadOnlyList<BillableService> All = new List<BillableService>
    {
        // --- Managed backup (agent/VM management via VSPC) ------------------
        new("managed_vms", "Managed Backup", "Managed VMs", "VMs",
            new[] { "ManagedVms" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("backedup_vms", "Managed Backup", "Backed-up VMs", "VMs",
            new[] { "BackedupVms" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("managed_cdp_vms", "Managed Backup", "CDP-protected VMs", "VMs",
            new[] { "ManagedCdpVms" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("managed_server_agents", "Managed Backup", "Managed server agents", "agents",
            new[] { "ManagedServerAgents" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("managed_workstation_agents", "Managed Backup", "Managed workstation agents", "agents",
            new[] { "ManagedWorkstationAgents" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("managed_users", "Managed Backup", "Managed users", "users",
            new[] { "ManagedUsers" }, UsageAggregation.LastDay, UnitConversion.Count),

        // --- Veeam Cloud Connect backup -------------------------------------
        new("vm_cloud_backups", "Cloud Connect Backup", "VM cloud backups", "VMs",
            new[] { "VmCloudBackups" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("server_cloud_backups", "Cloud Connect Backup", "Server agent cloud backups", "agents",
            new[] { "ServerCloudBackups" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("workstation_cloud_backups", "Cloud Connect Backup", "Workstation agent cloud backups", "agents",
            new[] { "WorkstationCloudBackups" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("cloud_storage_gb", "Cloud Connect Backup", "Cloud repository consumption", "GB",
            new[] { "CloudTotalUsage" }, UsageAggregation.LastDay, UnitConversion.BytesToGb),
        new("cloud_transfer_out_gb", "Cloud Connect Backup", "Data transfer out", "GB",
            new[] { "VbrCloudBackupsDataTransferOut", "AgentCloudBackupDataTransferOut" },
            UsageAggregation.SumOverPeriod, UnitConversion.BytesToGb),

        // --- Veeam Cloud Connect replication ---------------------------------
        new("vm_cloud_replicas", "Cloud Connect Replication", "VM cloud replicas", "VMs",
            new[] { "VmCloudReplicas" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("replica_storage_gb", "Cloud Connect Replication", "Replica storage consumption", "GB",
            new[] { "VmCloudReplicaStorageUsage" }, UsageAggregation.LastDay, UnitConversion.BytesToGb),

        // --- Microsoft 365 backup --------------------------------------------
        new("vb365_protected_users", "Microsoft 365 Backup", "Protected Microsoft 365 users", "users",
            new[] { "Vb365ProtectedUsers" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("vb365_backup_gb", "Microsoft 365 Backup", "Microsoft 365 backup size", "GB",
            new[] { "Vb365BackupSize" }, UsageAggregation.LastDay, UnitConversion.BytesToGb),

        // --- File share & object storage backup -------------------------------
        new("protected_file_shares", "File Share Backup", "Protected file shares", "shares",
            new[] { "ProtectedFileShares" }, UsageAggregation.LastDay, UnitConversion.Count),
        new("file_share_backup_gb", "File Share Backup", "File share backup size", "GB",
            new[] { "FileShareBackupSize" }, UsageAggregation.LastDay, UnitConversion.BytesToGb),

        // --- Public cloud backup ----------------------------------------------
        new("public_cloud_backup_gb", "Public Cloud Backup", "Public cloud backup size", "GB",
            new[] { "PublicCloudBackupSize" }, UsageAggregation.LastDay, UnitConversion.BytesToGb),
    };

    public static BillableService? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// Computes billable units for a service from raw usage samples.
    /// LastDay: per counter type, take the value(s) at that counter's most recent date
    /// (summed across locations), then sum the counter types. SumOverPeriod: sum everything.
    /// </summary>
    public static long ComputeUnits(BillableService service, IEnumerable<UsageSample> samples)
    {
        var relevant = samples.Where(s => service.CounterTypes.Contains(s.CounterType)).ToList();
        if (relevant.Count == 0) return 0;

        long raw;
        if (service.Aggregation == UsageAggregation.SumOverPeriod)
        {
            raw = relevant.Sum(s => s.Value);
        }
        else
        {
            raw = 0;
            foreach (var group in relevant.GroupBy(s => s.CounterType))
            {
                var lastDate = group.Max(s => s.Date);
                raw += group.Where(s => s.Date == lastDate).Sum(s => s.Value);
            }
        }

        if (raw <= 0) return 0;
        return service.Conversion == UnitConversion.BytesToGb
            ? (raw + BytesPerGb - 1) / BytesPerGb   // round up to whole GB
            : raw;
    }
}
