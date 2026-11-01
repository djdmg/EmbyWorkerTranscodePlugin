namespace RemoteTranscode.Worker.Ui;

/// <summary>"Aide → À propos": the worker's version and what support needs to identify an installation.</summary>
public static class AboutDialog
{
    /// <summary>Product version (major.minor.patch), as set in the project file and shown by the installer.</summary>
    public static string Version
    {
        get
        {
            var v = typeof(AboutDialog).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static void Show(WorkerHost host, IWin32Window? owner = null)
    {
        var caps = host.Capabilities;
        string text =
            $"Emby Remote Worker {Version}\n\n" +
            $"Transcodage et encodage distribués pour Emby.\n\n" +
            $"Worker : {host.Config.Name}" + (host.Config.WorkerId is { } id ? $" (id {id})" : " (non associé)") + "\n" +
            $"Serveur : {host.Config.CoordinatorUrl ?? "aucun"}\n" +
            $"FFmpeg : {FirstLine(caps?.FfmpegVersion) ?? "non détecté"}\n" +
            $"Configuration et journaux : {WorkerConfig.Directory}\n" +
            $".NET : {Environment.Version}";
        MessageBox.Show(owner, text, "À propos d'Emby Remote Worker", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string? FirstLine(string? s) => s?.Split('\n')[0].Trim();
}
