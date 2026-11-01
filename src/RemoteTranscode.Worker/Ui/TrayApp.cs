using System.Drawing.Drawing2D;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker.Ui;

/// <summary>
/// Notification-area icon: colour reflects the state, the menu gives quick control. Everything here runs on the UI
/// thread; worker state is polled by a one-second WinForms timer rather than pushed by events.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly WorkerHost host;
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem jobsItem = new() { Enabled = false };
    private readonly ToolStripMenuItem availableItem = new("Disponible") { CheckOnClick = true };
    private readonly ToolStripMenuItem releaseItem = new("Libérer le PC maintenant");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Dictionary<WorkerState, Icon> icons = new();
    private readonly Control uiThread = new();
    private MainForm? form;
    private WorkerState? shownState;
    // Set while the code itself updates availableItem.Checked, whose CheckedChanged must then not count as a user change.
    private bool syncingAvailable;

    public TrayApp(WorkerHost host, int? openTab = null)
    {
        this.host = host;
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(jobsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(availableItem);
        menu.Items.Add(releaseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ouvrir…", null, (_, _) => ShowForm(0));
        menu.Items.Add("Paramètres…", null, (_, _) => ShowForm(1));
        menu.Items.Add("Journaux…", null, (_, _) => ShowForm(2));
        menu.Items.Add($"À propos (version {AboutDialog.Version})…", null, (_, _) => AboutDialog.Show(host));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, async (_, _) => await ExitAsync());

        SyncAvailableItem();
        availableItem.CheckedChanged += (_, _) =>
        {
            if (!syncingAvailable)
                SetEnabled(availableItem.Checked);
        };
        releaseItem.Click += async (_, _) =>
        {
            if (ConfirmRelease())
                await host.ReleaseNowAsync();
        };

        uiThread.CreateControl();   // marshalling target for requests coming from other threads
        icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Emby Remote Worker" };
        icon.DoubleClick += (_, _) => ShowForm(0);
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();

        if (!host.IsPaired)
            ShowForm(1);
        else if (openTab is { } tab)
            ShowForm(tab);
        _ = host.StartAsync();
    }

    /// <summary>
    /// User switch, from the tray item or the window's check box.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (host.Service != null)
            host.Service.Enabled = enabled;
        else
        {
            host.Config.Available = enabled;
            try
            {
                host.Config.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                host.Logger.Log($"configuration non enregistrée : {ex.Message}");
            }
        }
        SyncAvailableItem();
        host.Logger.Log(enabled ? "worker activé par l'utilisateur" : "worker désactivé par l'utilisateur");
    }

    /// <summary>Mirrors the configuration in the menu item without it being taken for a user click.</summary>
    private void SyncAvailableItem()
    {
        if (availableItem.Checked == host.Config.Available)
            return;
        syncingAvailable = true;
        try { availableItem.Checked = host.Config.Available; }
        finally { syncingAvailable = false; }
    }

    public static bool ConfirmRelease() =>
        MessageBox.Show("Toutes les tâches en cours vont être arrêtées. Les lectures reprendront sur un autre worker ou sur le serveur, " +
                        "avec une interruption possible. Continuer ?", "Libérer le PC", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    private void Refresh()
    {
        var (state, reason) = host.Status();
        int jobs = host.Service?.Jobs.Count ?? 0;
        statusItem.Text = $"État : {Labels.State(state)}{(reason is null ? "" : $" — {reason}")}";
        jobsItem.Text = jobs == 0 ? "Aucune tâche en cours" : $"{jobs} tâche(s) en cours";
        releaseItem.Enabled = jobs > 0;
        SyncAvailableItem();

        string tip = $"Emby Remote Worker — {Labels.State(state)}" + (jobs > 0 ? $" ({jobs} tâche(s))" : "");
        // NotifyIcon.Text throws above 63 characters.
        icon.Text = tip.Length > 63 ? tip[..63] : tip;
        if (shownState != state)
        {
            icon.Icon = IconFor(state);
            shownState = state;
        }
    }

    /// <summary>Called from a background thread when the executable is launched again.</summary>
    public void ShowFromAnotherInstance() => uiThread.BeginInvoke(() => ShowForm(0));

    /// <summary>Same as the menu's "Quitter", asked by the setup or the uninstall through the "-exit" event.</summary>
    public void ExitFromAnotherInstance() => uiThread.BeginInvoke(async () => await ExitAsync());

    private void ShowForm(int tab)
    {
        if (form is null || form.IsDisposed)
            form = new MainForm(host, this);
        form.SelectTab(tab);
        form.Show();
        form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    private async Task ExitAsync()
    {
        int jobs = host.Service?.Jobs.Count ?? 0;
        if (jobs > 0 && !ConfirmRelease())
            return;
        timer.Stop();
        await host.StopAsync();
        icon.Visible = false;
        icon.Dispose();
        ExitThread();
    }

    private Icon IconFor(WorkerState state)
    {
        if (icons.TryGetValue(state, out var cached))
            return cached;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Labels.Color(state));
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3);
            g.DrawEllipse(pen, 2, 2, 28, 28);
            // Play triangle cut into slices, as in the application icon (Assets\worker.ico).
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, [new PointF(11, 8), new PointF(15, 10.2f), new PointF(15, 21.8f), new PointF(11, 24)]);
            g.FillPolygon(white, [new PointF(16.5f, 11), new PointF(19.5f, 12.6f), new PointF(19.5f, 19.4f), new PointF(16.5f, 21)]);
            g.FillPolygon(white, [new PointF(21, 13.4f), new PointF(25, 16), new PointF(21, 18.6f)]);
        }
        // The HICON is never destroyed: one per state, cached for the lifetime of the process.
        var result = Icon.FromHandle(bmp.GetHicon());
        icons[state] = result;
        return result;
    }
}

public static class Labels
{
    public static string State(WorkerState s) => s switch
    {
        WorkerState.Offline => "hors ligne",
        WorkerState.Unavailable => "indisponible",
        WorkerState.Available => "disponible",
        WorkerState.Busy => "occupé",
        WorkerState.Draining => "en drainage",
        WorkerState.Error => "en erreur",
        _ => s.ToString(),
    };

    /// <summary>A remaining time, short: "45 s", "3 min", "1 h 20".</summary>
    public static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:0} s" : seconds < 3600 ? $"{seconds / 60:0} min" : $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00}";

    public static Color Color(WorkerState s) => s switch
    {
        WorkerState.Available => System.Drawing.Color.FromArgb(46, 160, 67),
        WorkerState.Busy => System.Drawing.Color.FromArgb(31, 111, 235),
        WorkerState.Draining => System.Drawing.Color.FromArgb(219, 138, 0),
        WorkerState.Error => System.Drawing.Color.FromArgb(207, 34, 46),
        _ => System.Drawing.Color.FromArgb(110, 118, 129),
    };
}
