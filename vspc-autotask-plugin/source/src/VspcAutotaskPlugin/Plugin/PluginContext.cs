using System.Security.Cryptography;
using System.Xml.Linq;
using Veeam.AC.Service.PluginManager;

namespace VspcAutotaskPlugin.Plugin;

/// <summary>
/// Detects whether the service runs under VSPC plugin hosting (the VSPC service sets
/// GRPC_PLUGIN_REGISTRY_ADDRESS / GRPC_PLUGIN_REGISTRY_PORT) and loads the plugin
/// manifest + nuspec metadata into the gRPC <see cref="PluginManifest"/> message that
/// plugin registration sends to the VSPC Plugin Registry.
/// </summary>
public sealed class PluginContext
{
    /// <summary>True when the VSPC plugin manager exported registry environment variables.
    /// They are optional hints: the Plugin Registry listens on loopback port 5017 by
    /// convention (same default as the SDK template), so their absence is not an error.</summary>
    public bool HasHostEnvironment { get; private init; }
    public string RegistryAddress { get; private init; } = "";
    public PluginManifest? Manifest { get; private init; }
    /// <summary>Directory that contains the package "content" tree, when found.</summary>
    public string? PackageRoot { get; private init; }

    public static PluginContext Load(IConfiguration config)
    {
        var registryAddress = config["GRPC_PLUGIN_REGISTRY_ADDRESS"];
        var registryPort = config["GRPC_PLUGIN_REGISTRY_PORT"];
        var hasHostEnvironment = !string.IsNullOrEmpty(registryAddress) || !string.IsNullOrEmpty(registryPort);
        if (string.IsNullOrEmpty(registryAddress))
            registryAddress = $"http://localhost:{(string.IsNullOrEmpty(registryPort) ? "5017" : registryPort)}";

        var (manifest, packageRoot) = TryLoadManifest();
        return new PluginContext
        {
            HasHostEnvironment = hasHostEnvironment,
            RegistryAddress = registryAddress,
            Manifest = manifest,
            PackageRoot = packageRoot
        };
    }

    /// <summary>
    /// Locates the plugin manifest and the package content root. These are found
    /// independently: the manifest may sit next to the executable (a defensive copy)
    /// while the package content tree — which the service serves at /content so VSPC can
    /// fetch UI assets — lives several levels up (…/&lt;version&gt;/content/any/any/service/exe).
    /// </summary>
    private static (PluginManifest? Manifest, string? PackageRoot) TryLoadManifest()
    {
        var probeRoots = new List<string> { AppContext.BaseDirectory };
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 5; i++)
        {
            try { dir = Path.GetFullPath(Path.Combine(dir, "..")); }
            catch { break; }
            probeRoots.Add(dir);
        }
        probeRoots.Add(Directory.GetCurrentDirectory());
        var roots = probeRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Package root = the first probed directory that actually contains the package
        // content tree (content/any/any). Independent of where the manifest is found.
        string? packageRoot = roots.FirstOrDefault(root =>
            File.Exists(Path.Combine(root, "content", "any", "any", "plugin.manifest.xml")));

        // Manifest for reading: prefer the one inside the content tree, else a copy next
        // to the executable.
        var manifestPath = roots
            .Select(root => Path.Combine(root, "content", "any", "any", "plugin.manifest.xml"))
            .Concat(roots.Select(root => Path.Combine(root, "plugin.manifest.xml")))
            .FirstOrDefault(File.Exists);

        if (manifestPath == null)
            return (null, null);

        try
        {
            return (ReadManifest(manifestPath, packageRoot ?? AppContext.BaseDirectory, packageRoot), packageRoot);
        }
        catch
        {
            return (null, null);
        }
    }

    private static PluginManifest ReadManifest(string manifestPath, string root, string? packageRoot)
    {
        var pluginElement = XDocument.Load(manifestPath).Element("plugin")
            ?? throw new InvalidOperationException("plugin.manifest.xml is missing the <plugin> root element");

        string? Get(string name) => pluginElement.Element(name)?.Value;
        string Req(string name) => Get(name) is { Length: > 0 } v
            ? v : throw new InvalidOperationException($"plugin.manifest.xml is missing required <{name}>");

        // Package metadata comes from the nuspec when present, with safe fallbacks for dev runs.
        var (packageId, version, description, icon) = ReadNuspec(root);

        var manifest = new PluginManifest
        {
            ManifestVersion = int.TryParse(pluginElement.Attribute("manifestVersion")?.Value, out var mv) ? mv : 1,
            PluginId = Req("pluginId"),
            PackageId = packageId,
            Name = Req("pluginName"),
            Description = description,
            Version = version,
            Icon = Get("iconUrl") ?? icon,
            ServiceHttpPort = int.Parse(Req("serviceHttpPort")),
            ServiceExecutableName = Get("serviceExecutableName") ?? "",
            ServiceStartArguments = Get("serviceStartArguments") ?? "",
            ServiceOsServiceName = Get("serviceOsServiceName") ?? "",
            ServiceHosting = Enum.Parse<PluginHosting>(Get("serviceHosting") ?? "external", ignoreCase: true),
            ServiceApiKeyObtainMethod = Enum.Parse<PluginApiKeyObtainMethod>(Get("serviceApiKeyObtainMethod") ?? "none", ignoreCase: true),
            ServiceHealthCheckUrl = Get("serviceHealthCheckUrl") ?? "",
            ServiceReadinessCheckUrl = Get("serviceReadinessCheckUrl") ?? "",
            ServicePurgeDataRequestUrl = Get("servicePurgeDataRequestUrl") ?? "",
            ServiceLogDownloadRequestUrl = Get("serviceLogDownloadRequestUrl") ?? "",
            ServiceUseDb = bool.TryParse(Get("serviceUseDb"), out var useDb) && useDb
        };

        if (packageRoot != null)
        {
            foreach (var file in EnumerateContentFiles(Path.Combine(packageRoot, "content")))
                manifest.Files.Add(file);
        }
        return manifest;
    }

    private static (string PackageId, string Version, string Description, string Icon) ReadNuspec(string root)
    {
        var fallbackVersion = typeof(PluginContext).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var fallback = ("VspcAutotaskPlugin", fallbackVersion,
            "Autotask PSA integration for Veeam Service Provider Console",
            "content/any/any/ui-content/favicon.png");

        var nuspecPath = Directory.EnumerateFiles(root, "*.nuspec", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (nuspecPath == null) return fallback;
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd";
            var metadata = XDocument.Load(nuspecPath).Element(ns + "package")?.Element(ns + "metadata");
            if (metadata == null) return fallback;
            return (
                metadata.Element(ns + "id")?.Value ?? fallback.Item1,
                metadata.Element(ns + "version")?.Value ?? fallback.Item2,
                metadata.Element(ns + "description")?.Value ?? fallback.Item3,
                (metadata.Element(ns + "icon")?.Value ?? fallback.Item4).Replace('\\', '/'));
        }
        catch
        {
            return fallback;
        }
    }

    private static IEnumerable<PluginFile> EnumerateContentFiles(string contentDir)
    {
        var baseDir = Path.GetDirectoryName(contentDir.TrimEnd(Path.DirectorySeparatorChar, '/'))!;
        foreach (var path in Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories))
        {
            // content/<platform>/<architecture>/<purpose>/...
            var relative = Path.GetRelativePath(baseDir, path).Replace('\\', '/');
            var parts = relative.Split('/');
            if (parts.Length < 5) continue;

            PluginFilePurpose purpose;
            switch (parts[3])
            {
                case "service": purpose = PluginFilePurpose.ServiceContent; break;
                case "agent": purpose = PluginFilePurpose.AgentContent; break;
                case "ui-script": purpose = PluginFilePurpose.UiScript; break;
                case "ui-content": purpose = PluginFilePurpose.UiContent; break;
                case "ui-layout-config": purpose = PluginFilePurpose.UiLayoutConfig; break;
                case "rest-specification": purpose = PluginFilePurpose.RestSpecification; break;
                case "grpc-specification": purpose = PluginFilePurpose.GrpcSpecification; break;
                case "json-rpc-specification": purpose = PluginFilePurpose.JsonRpcSpecification; break;
                default: continue;
            }

            using var stream = File.OpenRead(path);
            using var sha1 = SHA1.Create();
            var sha1Hash = Convert.ToBase64String(sha1.ComputeHash(stream));
            stream.Position = 0;
            using var sha256 = SHA256.Create();
            var sha256Hash = Convert.ToBase64String(sha256.ComputeHash(stream));

            yield return new PluginFile
            {
                Path = relative,
                Sha1Hash = sha1Hash,
                Sha256Hash = sha256Hash,
                Purpose = purpose
            };
        }
    }
}
