using System.Reflection;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StrmDownload.Tests;

public sealed class PluginMetadataTests
{
    [Fact]
    public void CatalogTemplateUsesThePluginIdentityAndJellyfinSchema()
    {
        var catalog = JsonSerializer.Deserialize<PackageInfo[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "manifest.json")))!;
        var package = Assert.Single(catalog);
        var paths = DispatchProxy.Create<IApplicationPaths, ApplicationPathsProxy>();
        var plugin = new Plugin(paths);
        Assert.Equal(plugin.Id, package.Id);
        Assert.Equal(plugin.Name, package.Name);
        Assert.NotEmpty(package.Overview);
        Assert.NotEmpty(package.Owner);
        Assert.Empty(package.Versions);
    }

    [Fact]
    public void HostCreatedPluginCanBeSerializedInPluginListing()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, ApplicationPathsProxy>();
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        using var provider = services.BuildServiceProvider();
        var plugin = ActivatorUtilities.CreateInstance<Plugin>(provider);
        var assembly = typeof(Plugin).Assembly;
        var local = new LocalPlugin(Path.GetDirectoryName(assembly.Location)!, true, new PluginManifest
        {
            Id = plugin.Id,
            Name = plugin.Name,
            Version = assembly.GetName().Version!.ToString()
        })
        { Instance = plugin };

        // Match /Plugins: metadata is obtained lazily while the list is serialized.
        var json = JsonSerializer.Serialize(new[] { local }.Select(p => p.GetPluginInfo()));

        Assert.Contains("STRM Download", json);
        Assert.Equal(assembly.Location, plugin.AssemblyFilePath);
        Assert.Equal(assembly.GetName().Version, plugin.Version);
        Assert.Equal(Path.Combine(paths.PluginsPath, Path.GetFileNameWithoutExtension(assembly.Location)), plugin.DataFolderPath);
        Assert.Equal(Path.GetDirectoryName(assembly.Location) != Path.GetDirectoryName(typeof(BasePlugin).Assembly.Location),
            plugin.CanUninstall);
    }

    public class ApplicationPathsProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_PluginsPath"
                ? Path.Combine(Path.GetTempPath(), "strm-download-test-plugins")
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
