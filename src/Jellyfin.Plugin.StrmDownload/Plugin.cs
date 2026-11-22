using System.Runtime.InteropServices;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;

[assembly: Guid("e8c052f3-3b7c-47f7-979a-2768dc1fb41c")]

namespace Jellyfin.Plugin.StrmDownload;

public sealed class Plugin : BasePlugin
{
    public Plugin(IApplicationPaths applicationPaths)
    {
        // Unlike BasePlugin<TConfiguration>, BasePlugin does not initialize this metadata.
        // Jellyfin reads it during plugin startup and when serializing the /Plugins response.
        var assembly = typeof(Plugin).Assembly;
        SetAttributes(assembly.Location,
            Path.Combine(applicationPaths.PluginsPath, Path.GetFileNameWithoutExtension(assembly.Location)),
            assembly.GetName().Version!);
    }

    public override string Name => "STRM Download";
    public override string Description => "Download the HTTP media referenced by STRM files.";
    public override Guid Id => new("e8c052f3-3b7c-47f7-979a-2768dc1fb41c");
}
