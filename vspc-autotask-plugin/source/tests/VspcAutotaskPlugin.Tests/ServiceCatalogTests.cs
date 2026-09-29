using VspcAutotaskPlugin.Domain;
using Xunit;

namespace VspcAutotaskPlugin.Tests;

public class ServiceCatalogTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Catalog_keys_are_unique()
    {
        var keys = ServiceCatalog.All.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void LastDay_uses_only_the_most_recent_date_and_sums_locations()
    {
        var service = ServiceCatalog.Find("managed_vms")!;
        var samples = new List<UsageSample>
        {
            new(new DateOnly(2026, 7, 15), "ManagedVms", 10),
            new(new DateOnly(2026, 7, 16), "ManagedVms", 12),
            new(new DateOnly(2026, 7, 16), "ManagedVms", 3), // second location, same day
            new(new DateOnly(2026, 7, 16), "BackedupVms", 99) // different counter, ignored
        };
        Assert.Equal(15, ServiceCatalog.ComputeUnits(service, samples));
    }

    [Fact]
    public void LastDay_takes_each_counter_type_at_its_own_last_date()
    {
        var service = new BillableService("test", "g", "Test", "u",
            new[] { "A", "B" }, UsageAggregation.LastDay, UnitConversion.Count);
        var samples = new List<UsageSample>
        {
            new(new DateOnly(2026, 7, 10), "A", 5),
            new(new DateOnly(2026, 7, 12), "A", 7),
            new(new DateOnly(2026, 7, 9), "B", 4)
        };
        Assert.Equal(11, ServiceCatalog.ComputeUnits(service, samples));
    }

    [Fact]
    public void SumOverPeriod_sums_all_samples_across_types()
    {
        var service = ServiceCatalog.Find("cloud_transfer_out_gb")!;
        var samples = new List<UsageSample>
        {
            new(new DateOnly(2026, 7, 1), "VbrCloudBackupsDataTransferOut", Gb / 2),
            new(new DateOnly(2026, 7, 2), "AgentCloudBackupDataTransferOut", (Gb / 4) * 3)
        };
        // 0.5 GB + 0.75 GB = 1.25 GB -> rounds up to 2
        Assert.Equal(2, ServiceCatalog.ComputeUnits(service, samples));
    }

    [Fact]
    public void BytesToGb_rounds_up_to_whole_units()
    {
        var service = ServiceCatalog.Find("cloud_storage_gb")!;
        Assert.Equal(1, ServiceCatalog.ComputeUnits(service,
            new[] { new UsageSample(new DateOnly(2026, 7, 16), "CloudTotalUsage", Gb) }));
        Assert.Equal(2, ServiceCatalog.ComputeUnits(service,
            new[] { new UsageSample(new DateOnly(2026, 7, 16), "CloudTotalUsage", Gb + 1) }));
    }

    [Fact]
    public void No_samples_means_zero_units()
    {
        var service = ServiceCatalog.Find("vb365_protected_users")!;
        Assert.Equal(0, ServiceCatalog.ComputeUnits(service, Array.Empty<UsageSample>()));
        Assert.Equal(0, ServiceCatalog.ComputeUnits(service,
            new[] { new UsageSample(new DateOnly(2026, 7, 16), "SomethingElse", 5) }));
    }
}
