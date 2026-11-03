using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace RemoteTranscode.EmbyPlugin;

/// <summary>
/// Emby side of the distributed transcoding: an administration page in the dashboard and an admin-only API that
/// relays to the coordinator. Emby keeps authenticating users; the coordinator is never exposed to clients.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = new("3c4f1d9e-6b2a-4e8f-9a51-7d2c0e6b4a11");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Transcodage distribué";

    public override string Description => "Déporte le transcodage et les conversions d'Emby vers des PC Windows (workers).";

    public override Guid Id => PluginId;

    /// <summary>Uninstalling must leave Emby exactly as it was: its own ffmpeg again.</summary>
    // Called by Emby only for an uninstall from its plugin catalogue; a DLL deleted by hand leaves the dispatcher
    // configured (it keeps working: without coordinator the relay runs Emby's ffmpeg).
    public override void OnUninstalling()
    {
        if (Runtime.Instance?.DispatcherPath is { } dispatcher)
            Interception.Instance?.Apply(false, dispatcher);
        base.OnUninstalling();
    }

    /// <summary>
    /// Plugin version in the page names: Emby serves plugin pages under a URL that only changes with Emby's version,
    /// so without it browsers would keep showing the previous plugin's page.
    /// The csproj computes it once (PageSuffix) for both the script reference inside the page and this assembly
    /// metadata, so the two can never diverge (prerelease or 4-part versions included).
    /// </summary>
    private static string PageSuffix => typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "PageSuffix")?.Value ?? "";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "remotetranscode" + PageSuffix,
            DisplayName = "Transcodage distribué",
            EmbeddedResourcePath = GetType().Namespace + ".Web.remotetranscode.html",
            EnableInMainMenu = true,
            MenuIcon = "dns",
        },
        new PluginPageInfo
        {
            Name = "remotetranscode" + PageSuffix + "js",
            EmbeddedResourcePath = GetType().Namespace + ".Web.remotetranscode.js",
        },
        new PluginPageInfo
        {
            Name = "remotetranscodemonitor" + PageSuffix,
            DisplayName = "Suivi du transcodage",
            EmbeddedResourcePath = GetType().Namespace + ".Web.remotetranscodemonitor.html",
            EnableInMainMenu = true,
            MenuIcon = "assessment",
        },
        new PluginPageInfo
        {
            Name = "remotetranscodemonitor" + PageSuffix + "js",
            EmbeddedResourcePath = GetType().Namespace + ".Web.remotetranscodemonitor.js",
        },
        new PluginPageInfo
        {
            Name = "librarydiagnostic" + PageSuffix,
            DisplayName = "Diagnostic de la bibliothèque",
            EmbeddedResourcePath = GetType().Namespace + ".Web.librarydiagnostic.html",
            EnableInMainMenu = true,
            MenuIcon = "assessment",
        },
        new PluginPageInfo
        {
            Name = "librarydiagnostic" + PageSuffix + "js",
            EmbeddedResourcePath = GetType().Namespace + ".Web.librarydiagnostic.js",
        },
    ];
}

/// <summary>Stored by Emby as XML in plugins/configurations/RemoteTranscode.EmbyPlugin.xml.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// When true, Emby hands its ffmpeg runs (playback transcodes, conversions) to the dispatcher, which sends them to
    /// available workers and falls back to Emby's own ffmpeg otherwise. When false, Emby is untouched.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where workers connect. "https://0.0.0.0:8097": served directly with a self-signed certificate that the workers
    /// pin. "http://127.0.0.1:8097": behind a TLS reverse proxy whose address is <see cref="PublicUrl"/>.
    /// </summary>
    public string WorkerUrl { get; set; } = "https://0.0.0.0:8097";

    /// <summary>Public HTTPS address of the reverse proxy in front of <see cref="WorkerUrl"/>, if any.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Host name or IP address the workers use to reach this server in direct-TLS mode (pairing codes, and added to
    /// the certificate when it is created). Empty: detected (LAN address of the interface with the default gateway).
    /// </summary>
    public string? AdvertisedHost { get; set; }

    /// <summary>Loopback port of the relay and administration endpoint (never exposed).</summary>
    public int ShimPort { get; set; } = 8098;

    /// <summary>
    /// Failed conversions are reported as anonymous issues on the project's GitHub repository (see FailureReporter).
    /// On by default; does nothing in a build without a report token.
    /// </summary>
    public bool ReportFailures { get; set; } = true;

    /// <summary>
    /// Converts a conversion's audio ahead on the server (CoordinatorOptions.EncodeAudioAhead). Off until validated on
    /// a real server; switched on the monitoring page, through POST /RemoteTranscode/AudioAhead.
    /// </summary>
    public bool EncodeAudioAhead { get; set; } = false;
}
