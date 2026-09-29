using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Infrastructure;
using Xunit;

namespace VspcAutotaskPlugin.Tests;

/// <summary>
/// A freshly installed plugin is always in the "Autotask not connected" state until the
/// user completes the connection wizard. These tests verify that the read accessors the
/// plugin UI drives on load (company/service/billing-code/contract lists and picklists)
/// degrade to empty results instead of throwing "Autotask connection is not configured".
/// That is what lets the Companies/Billing/Ticketing/Settings pages render clean empty
/// grids and dropdowns pre-connection rather than surfacing 500s as error toasts.
/// </summary>
public sealed class AutotaskClientTests : IDisposable
{
    private readonly string _dir;
    private readonly AutotaskClient _client;

    public AutotaskClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vspc-at-tests-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir })
            .Build();
        var paths = new AppPaths(config, new TestHostEnvironment(_dir));
        var db = new Db(paths);
        var protector = new SecretProtector(paths);
        _client = new AutotaskClient(db, protector, NullLogger<AutotaskClient>.Instance);
    }

    [Fact]
    public void IsConfigured_is_false_without_a_stored_connection() =>
        Assert.False(_client.IsConfigured);

    [Fact]
    public async Task GetActiveCompaniesAsync_returns_empty_when_not_configured() =>
        Assert.Empty(await _client.GetActiveCompaniesAsync());

    [Fact]
    public async Task GetActiveServicesAsync_returns_empty_when_not_configured() =>
        Assert.Empty(await _client.GetActiveServicesAsync());

    [Fact]
    public async Task GetBillingCodesAsync_returns_empty_when_not_configured() =>
        Assert.Empty(await _client.GetBillingCodesAsync());

    [Fact]
    public async Task GetCompanyContractsAsync_returns_empty_when_not_configured() =>
        Assert.Empty(await _client.GetCompanyContractsAsync(companyId: 12345));

    [Fact]
    public async Task GetPicklistAsync_returns_empty_when_not_configured() =>
        Assert.Empty(await _client.GetPicklistAsync("Tickets", "status"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup of the temp SQLite database */ }
    }

    /// <summary>Minimal host environment; AppPaths uses only the (rooted) DataDir here.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string root) => ContentRootPath = root;
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "VspcAutotaskPlugin.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
