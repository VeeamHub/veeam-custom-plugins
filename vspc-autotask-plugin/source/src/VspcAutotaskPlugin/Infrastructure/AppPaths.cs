namespace VspcAutotaskPlugin.Infrastructure;

/// <summary>Resolves the writable data directory used for the SQLite database and key material.</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, "plugin.db");
    public string AesKeyPath => Path.Combine(DataDir, "secret.key");

    public AppPaths(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["DataDir"];
        // Default lives outside the package folder so plugin upgrades (which redeploy
        // the package) don't destroy mappings and ticket links.
        DataDir = !string.IsNullOrEmpty(configured)
            ? (Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured))
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VspcAutotaskPlugin");
        Directory.CreateDirectory(DataDir);
    }
}
