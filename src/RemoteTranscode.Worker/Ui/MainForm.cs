using System.Diagnostics;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker.Ui;

/// <summary>
/// Status, settings and logs of the worker. Closing the window only hides it; the worker keeps running.
/// Runs on the UI thread: job and load figures are polled every second, log lines are marshalled with BeginInvoke.
/// </summary>
public sealed class MainForm : Form
{
    private readonly WorkerHost host;
    private readonly TrayApp tray;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };

    // Status tab
    private readonly Label stateLabel = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 13, FontStyle.Bold) };
    private readonly Label reasonLabel = new() { AutoSize = true };
    private readonly Label serverLabel = new() { AutoSize = true };
    private readonly Label loadLabel = new() { AutoSize = true };
    private readonly Label queueLabel = new() { AutoSize = true };
    private readonly Label updateLabel = new() { AutoSize = true, MaximumSize = new Size(820, 0) };
    private readonly Label capsLabel = new() { AutoSize = true, MaximumSize = new Size(820, 0) };
    private readonly CheckBox enabledBox = new() { Text = "Autoriser l'utilisation de ce PC", AutoSize = true };
    private readonly Button releaseButton = new() { Text = "Libérer le PC maintenant", AutoSize = true };
    private readonly ListView jobsList = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };

    // Settings tab
    private readonly TextBox nameBox = new() { Width = 240 };
    private readonly Label coordinatorLabel = new() { AutoSize = true };
    private readonly TextBox pairingBox = new() { Width = 420, UseSystemPasswordChar = true };
    private readonly TextBox ffmpegBox = new() { Width = 420 };
    private readonly CheckBox hwBox = new() { Text = "Conversions : encodage par le GPU (plus rapide ; une reprise sur le serveur mélangerait deux encodeurs)", AutoSize = true };
    private readonly CheckBox hwPlaybackBox = new() { Text = "Lectures : encodage par le GPU (bien plus rapide sur un portable ; image du worker et du serveur un peu différentes)", AutoSize = true };
    private readonly CheckBox hwDecodeBox = new() { Text = "Décodage vidéo par le GPU quand l'encodage s'y fait (soulage le processeur ; image identique)", AutoSize = true };
    private readonly CheckBox gpuToneMapBox = new() { Text = "HDR → SDR (tone mapping) par le GPU quand il a été validé au démarrage (bien plus rapide, soulage le processeur)", AutoSize = true };
    private readonly NumericUpDown maxJobsBox = new() { Minimum = 1, Maximum = 16, Width = 60 };
    private readonly RadioButton manualRadio = new() { Text = "Manuel : je rends le PC disponible", AutoSize = true };
    private readonly RadioButton autoRadio = new() { Text = "Automatique : disponible après une inactivité de", AutoSize = true };
    private readonly NumericUpDown idleBox = new() { Minimum = 1, Maximum = 240, Width = 60 };
    private readonly CheckBox batteryBox = new() { Text = "Bloquer les traitements sur batterie", AutoSize = true };
    private readonly NumericUpDown cpuBox = Percent();
    private readonly NumericUpDown gpuBox = Percent();
    private readonly NumericUpDown memBox = Percent();
    private readonly NumericUpDown hysteresisBox = new() { Minimum = 0, Maximum = 600, Width = 60 };
    private readonly NumericUpDown stableBox = new() { Minimum = 0, Maximum = 600, Width = 60 };
    private readonly ComboBox reclaimBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Label reclaimWarning = new() { AutoSize = true, ForeColor = Color.FromArgb(154, 103, 0), MaximumSize = new Size(560, 0) };
    private readonly DataGridView scheduleGrid = new() { Height = 130, Width = 560, AllowUserToResizeRows = false, RowHeadersVisible = false };
    private readonly CheckBox startupBox = new() { Text = "Démarrer avec Windows", AutoSize = true };
    private readonly CheckBox autoUpdateBox = new() { Text = "Installer automatiquement les nouvelles versions du worker, quand il n'a aucune tâche en cours", AutoSize = true };
    // Each one restarts the worker: disabled while a start/stop/restart is in progress (see RefreshStatus).
    private readonly Button pairButton = new() { Text = "Associer", AutoSize = true };
    private readonly Button redetectButton = new() { Text = "Refaire la détection du matériel", AutoSize = true };
    private readonly Button saveButton = new() { Text = "Enregistrer", AutoSize = true };

    // Logs tab
    private readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };

    private static readonly string[] DayNames = ["Lu", "Ma", "Me", "Je", "Ve", "Sa", "Di"];
    private static readonly DayOfWeek[] DayOrder = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public MainForm(WorkerHost host, TrayApp tray)
    {
        this.host = host;
        this.tray = tray;
        Text = $"Emby Remote Worker {AboutDialog.Version}";
        // The exe's own icon (Assets\worker.ico) for the title bar and the taskbar button.
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (ArgumentException) { }
        Size = new Size(900, 640);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;

        tabs.TabPages.Add(BuildStatusTab());
        tabs.TabPages.Add(BuildSettingsTab());
        tabs.TabPages.Add(BuildLogsTab());
        Controls.Add(tabs);
        // Added after the tabs so it docks first, at the top; the tabs fill the rest.
        var help = new ToolStripMenuItem("Aide");
        help.DropDownItems.Add("Journaux…", null, (_, _) => SelectTab(2));
        help.DropDownItems.Add("Ouvrir le dossier de configuration", null, (_, _) => Process.Start("explorer.exe", WorkerConfig.Directory));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add("À propos d'Emby Remote Worker…", null, (_, _) => AboutDialog.Show(host, this));
        var menu = new MenuStrip();
        menu.Items.Add(help);
        MainMenuStrip = menu;
        Controls.Add(menu);

        LoadSettings();
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        RefreshStatus();
        host.Logger.LineWritten += OnLogLine;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        FormClosed += (_, _) => host.Logger.LineWritten -= OnLogLine;
    }

    public void SelectTab(int index)
    {
        tabs.SelectedIndex = index;
        // On first display WinForms focuses the first focusable control and may switch tabs: apply the choice again.
        if (!IsHandleCreated)
            Shown += (_, _) => { tabs.SelectedIndex = index; ActiveControl = tabs; };
    }

    // ---- Status --------------------------------------------------------------------------------

    private TabPage BuildStatusTab()
    {
        var page = new TabPage("État") { Padding = new Padding(10) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange([enabledBox, releaseButton]);
        top.Controls.AddRange([stateLabel, reasonLabel, serverLabel, loadLabel, queueLabel, updateLabel, capsLabel, actions,
            new Label { Text = "Tâches en cours", AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) }]);

        jobsList.Columns.Add("Fichier", 220);
        jobsList.Columns.Add("Type", 75);
        jobsList.Columns.Add("Encodeur", 85);
        jobsList.Columns.Add("Tranche", 70);
        jobsList.Columns.Add("Position", 70);
        jobsList.Columns.Add("Tranche %", 70);
        jobsList.Columns.Add("Total", 110);
        jobsList.Columns.Add("Vitesse", 60);
        jobsList.Columns.Add("Durée", 55);
        jobsList.Columns.Add("État", 120);

        // RefreshStatus also sets Checked from the config: only a real user change goes through SetEnabled.
        enabledBox.CheckedChanged += (_, _) =>
        {
            if (enabledBox.Checked != host.Config.Available)
                tray.SetEnabled(enabledBox.Checked);
        };
        releaseButton.Click += async (_, _) =>
        {
            if (TrayApp.ConfirmRelease())
                await host.ReleaseNowAsync();
        };

        var jobsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        jobsPanel.Controls.Add(jobsList);
        page.Controls.Add(jobsPanel);
        page.Controls.Add(top);
        return page;
    }

    private void RefreshStatus()
    {
        // Hidden in the tray most of the time: no point rebuilding the list.
        if (!Visible)
            return;
        bool idle = !host.Busy;
        pairButton.Enabled = redetectButton.Enabled = saveButton.Enabled = idle;
        var (state, reason) = host.Status();
        stateLabel.Text = Labels.State(state).ToUpperInvariant();
        stateLabel.ForeColor = Labels.Color(state);
        reasonLabel.Text = reason is null ? "Prêt à recevoir des tâches." : $"Raison : {reason}";
        var service = host.Service;
        serverLabel.Text = host.IsPaired
            ? $"Serveur : {host.Config.CoordinatorUrl} — {(service?.Connected == true ? "connecté" : "non connecté")}"
            : "Serveur : non associé (onglet Paramètres)";

        var l = service?.LastLoad;
        loadLabel.Text = l is null ? "Charge : —" :
            $"Charge — CPU {l.CpuPercent:0}% (dont tâches {l.OwnCpuPercent:0}%)   " +
            (l.GpuPercent is null ? "GPU : non mesurable (encodage CPU)   " : $"GPU {l.GpuPercent:0}% (tâches {l.OwnGpuPercent:0}%, autres {l.ForeignGpuPercent:0}%)   ") +
            $"Mémoire {l.MemoryPercent:0}%";

        // Emby's conversion queue, sent by the server every 30 s (plugin 0.7.17 and later; before, only with a conversion's progress).
        queueLabel.Text = service?.ConversionQueue is { } q
            ? $"File de conversion d'Emby : {q.Remaining} élément(s) à convertir" + (DateTime.Now - q.At > TimeSpan.FromMinutes(2) ? $" (au {q.At:HH:mm})" : "")
            : "File de conversion d'Emby : —";

        updateLabel.Text = $"Mises à jour : {host.Updater?.Status ?? "inactives"}";

        var caps = host.Capabilities;
        capsLabel.Text = caps is null ? "Capacités : détection en attente" :
            $"Encodeurs validés : {string.Join(", ", caps.VideoEncoders)}\nGPU : {string.Join("; ", caps.Gpus)}\n" +
            $"HDR → SDR : {(caps.GpuToneMapping is null ? "processeur (tone mapping GPU non validé)" : host.Config.GpuToneMapping ? "GPU (libplacebo)" : "processeur (GPU désactivé dans les paramètres)")}\n" +
            caps.FfmpegVersion;

        if (enabledBox.Checked != host.Config.Available)
            enabledBox.Checked = host.Config.Available;
        var jobs = service?.Jobs ?? [];
        releaseButton.Enabled = jobs.Count > 0;

        jobsList.BeginUpdate();
        jobsList.Items.Clear();
        foreach (var j in jobs.OrderBy(j => j.StartedAt))
        {
            var elapsed = DateTime.Now - j.StartedAt;
            // Times in the film: a conversion slice or a resumed playback starts mid-film, and ffmpeg's progress counts
            // from that start. Slice and total progress come with the coordinator's offer and updates (0.7.14 and later).
            var slice = j.Slice;
            string total = j.TotalPercent is { } tp
                ? $"{tp:0} %" + (j.TotalRemainingSeconds is { } left && left > 0 ? $", reste {Labels.Duration(left)}" : "")
                : "—";
            jobsList.Items.Add(new ListViewItem(
            [
                slice?.Title ?? j.JobId,
                j.Kind == JobKind.Playback ? "Lecture" : "Conversion",
                j.Encoder ?? "…",
                j.Kind == JobKind.Playback ? $"seg. {j.StartIndex}" : slice is null ? "—" : $"{slice.Number}/{slice.Count}",
                TimeSpan.FromSeconds(j.StartSeconds + j.MediaSeconds).ToString(@"h\:mm\:ss"),
                j.SlicePercent is { } sp ? $"{sp:0} %" : "—",
                j.Kind == JobKind.Playback ? "—" : total,
                j.Speed > 0 ? $"{j.Speed:0.0}x" : "—",
                elapsed.ToString(@"mm\:ss"),
                j.StopReason is null ? "en cours" : $"arrêt : {j.StopReason}",
            ]) { ToolTipText = slice is null ? null : $"{slice.Title} — tranche {slice.Number}/{slice.Count}" });
        }
        if (jobs.Count == 0)
            jobsList.Items.Add(new ListViewItem("Aucune tâche") { ForeColor = SystemColors.GrayText });
        jobsList.EndUpdate();
    }

    // ---- Settings ------------------------------------------------------------------------------

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Paramètres") { AutoScroll = true, Padding = new Padding(10) };
        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Top };

        pairButton.Click += async (_, _) => await WhileDisabledAsync(PairAsync);
        var browse = new Button { Text = "Parcourir…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Exécutables|*.exe", FileName = ffmpegBox.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                ffmpegBox.Text = dlg.FileName;
        };
        redetectButton.Click += async (_, _) => await WhileDisabledAsync(async () =>
        {
            try
            {
                CapabilityCache.Invalidate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                host.Logger.Log($"cache des capacités non supprimé : {ex.Message}");
            }
            await host.RestartAsync();
        });

        root.Controls.Add(Group("Serveur et association",
            Row("Nom du worker :", nameBox),
            Row("Coordinateur :", coordinatorLabel),
            Row("Code d'association :", pairingBox, pairButton),
            Hint("Le code est affiché par le serveur : Tableau de bord d'Emby → Transcodage distribué → Associer un worker. Il contient un secret révocable.")));

        // Right under the pairing, which is where a first installation opens: on by default, but seen.
        root.Controls.Add(Group("Mises à jour",
            Row(autoUpdateBox),
            Hint("Le worker consulte lui-même les publications du projet sur GitHub, télécharge le programme d'installation, vérifie son empreinte puis le lance, " +
                 "sans jamais revenir à une version plus ancienne. Le serveur Emby ne peut lui imposer aucun programme. " +
                 "Ces programmes ne sont pas signés : la confiance repose sur le dépôt GitHub du projet. Décochez pour installer vous-même chaque version (aucun contact avec GitHub).")));

        root.Controls.Add(Group("Traitements",
            Row("FFmpeg :", ffmpegBox, browse),
            Hint("Laisser vide pour utiliser le FFmpeg intégré au worker."),
            Row(hwBox),
            Row(hwPlaybackBox),
            Row(hwDecodeBox),
            Row(gpuToneMapBox),
            Row("Tâches simultanées maximum :", maxJobsBox),
            Row(redetectButton)));

        autoRadio.CheckedChanged += (_, _) => idleBox.Enabled = autoRadio.Checked;
        root.Controls.Add(Group("Disponibilité",
            Row(manualRadio),
            Row(autoRadio, idleBox, new Label { Text = "minutes sans clavier ni souris", AutoSize = true, Anchor = AnchorStyles.Left }),
            Row(batteryBox),
            Row("Bloquer si les autres applications utilisent plus de  CPU", cpuBox, Unit("%   GPU"), gpuBox, Unit("%   mémoire totale"), memBox, Unit("%")),
            Row("Temporisation des seuils :", hysteresisBox, Unit("s    Stabilisation avant disponibilité :"), stableBox, Unit("s"))));

        reclaimBox.Items.AddRange(["Terminer les lectures en cours", "Libérer immédiatement le PC"]);
        reclaimBox.SelectedIndexChanged += (_, _) => reclaimWarning.Text = reclaimBox.SelectedIndex == 1
            ? "Les traitements sont arrêtés dès que vous utilisez le PC. Une lecture en cours reprend sur un autre worker ou sur le serveur et peut subir une interruption."
            : "Les nouvelles tâches sont refusées ; les lectures en cours se terminent sur ce PC.";
        root.Controls.Add(Group("Quand j'utilise de nouveau le PC",
            Row(reclaimBox),
            Row(reclaimWarning)));

        foreach (string d in DayNames)
            scheduleGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = d, Width = 34 });
        scheduleGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Début (HH:mm)", Width = 110 });
        scheduleGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fin (HH:mm)", Width = 110 });
        root.Controls.Add(Group("Plages horaires autorisées",
            Row(scheduleGrid),
            Hint("Aucune ligne = à toute heure. Une fin avant le début (ex. 22:00 → 07:00) passe minuit.")));

        var cancel = new Button { Text = "Annuler", AutoSize = true };
        saveButton.Click += async (_, _) => await WhileDisabledAsync(SaveSettingsAsync);
        cancel.Click += (_, _) => LoadSettings();
        root.Controls.Add(Row(startupBox));
        root.Controls.Add(Row(saveButton, cancel));

        page.Controls.Add(root);
        return page;
    }

    private void LoadSettings()
    {
        var c = host.Config;
        var a = c.Availability;
        nameBox.Text = c.Name;
        coordinatorLabel.Text = c.CoordinatorUrl is null ? "non associé" : $"{c.CoordinatorUrl} (id {c.WorkerId})";
        pairingBox.Text = "";
        ffmpegBox.Text = c.FfmpegPath;
        hwBox.Checked = c.HardwareEncodingForConversions;
        hwPlaybackBox.Checked = c.HardwareEncodingForPlayback;
        hwDecodeBox.Checked = c.HardwareDecoding;
        gpuToneMapBox.Checked = c.GpuToneMapping;
        maxJobsBox.Value = Math.Clamp(c.MaxJobs, 1, 16);
        manualRadio.Checked = a.Mode == AvailabilityMode.Manual;
        autoRadio.Checked = a.Mode == AvailabilityMode.Automatic;
        idleBox.Value = Math.Clamp(a.IdleMinutes, 1, 240);
        idleBox.Enabled = autoRadio.Checked;
        batteryBox.Checked = a.BlockOnBattery;
        cpuBox.Value = (decimal)Math.Clamp(a.MaxForeignCpuPercent, 1, 100);
        gpuBox.Value = (decimal)Math.Clamp(a.MaxForeignGpuPercent, 1, 100);
        memBox.Value = (decimal)Math.Clamp(a.MaxMemoryPercent, 1, 100);
        hysteresisBox.Value = Math.Clamp(a.LoadHysteresisSeconds, 0, 600);
        stableBox.Value = Math.Clamp(a.StableSeconds, 0, 600);
        reclaimBox.SelectedIndex = a.Reclaim == ReclaimBehavior.ReleaseImmediately ? 1 : 0;
        scheduleGrid.Rows.Clear();
        foreach (var w in a.Schedule)
        {
            var cells = DayOrder.Select(d => (object)w.Days.Contains(d)).Concat([w.Start.ToString("HH:mm"), w.End.ToString("HH:mm")]).ToArray();
            scheduleGrid.Rows.Add(cells);
        }
        startupBox.Checked = WorkerHost.StartWithWindows;
        autoUpdateBox.Checked = c.AutoUpdate;
    }

    private async Task SaveSettingsAsync()
    {
        if (!TryReadSchedule(out var schedule, out string? error))
        {
            MessageBox.Show(this, error, "Plages horaires", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var c = host.Config;
        // The name and capabilities are only sent in the hello, and the ffmpeg path decides the capabilities: those need
        // a reconnection, as does GPU tone mapping (announced in the hello). Everything else is read live (availability via
        // ApplySettings, MaxJobs and GPU option per use).
        bool restart = c.FfmpegPath != ffmpegBox.Text.Trim() || c.Name != nameBox.Text.Trim() || c.GpuToneMapping != gpuToneMapBox.Checked;
        c.GpuToneMapping = gpuToneMapBox.Checked;
        c.Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : Environment.MachineName;
        c.FfmpegPath = ffmpegBox.Text.Trim();
        c.HardwareEncodingForConversions = hwBox.Checked;
        c.HardwareEncodingForPlayback = hwPlaybackBox.Checked;
        c.HardwareDecoding = hwDecodeBox.Checked;
        c.MaxJobs = (int)maxJobsBox.Value;
        c.AutoUpdate = autoUpdateBox.Checked;
        c.Availability = new AvailabilitySettings
        {
            Mode = autoRadio.Checked ? AvailabilityMode.Automatic : AvailabilityMode.Manual,
            IdleMinutes = (int)idleBox.Value,
            BlockOnBattery = batteryBox.Checked,
            MaxForeignCpuPercent = (double)cpuBox.Value,
            MaxForeignGpuPercent = (double)gpuBox.Value,
            MaxMemoryPercent = (double)memBox.Value,
            LoadHysteresisSeconds = (int)hysteresisBox.Value,
            StableSeconds = (int)stableBox.Value,
            Reclaim = reclaimBox.SelectedIndex == 1 ? ReclaimBehavior.ReleaseImmediately : ReclaimBehavior.FinishPlayback,
            Schedule = schedule,
        };
        try
        {
            c.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Les paramètres n'ont pas pu être enregistrés :\n{ex.Message}", "Paramètres", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        try
        {
            WorkerHost.StartWithWindows = startupBox.Checked;
        }
        catch (Exception ex)
        {
            host.Logger.Log($"démarrage automatique non modifié : {ex.Message}");
        }
        host.Service?.ApplySettings();
        host.Logger.Log("paramètres enregistrés");
        if (restart)
            await host.RestartAsync();
        LoadSettings();
    }

    private bool TryReadSchedule(out List<TimeWindow> schedule, out string? error)
    {
        schedule = [];
        error = null;
        foreach (DataGridViewRow row in scheduleGrid.Rows)
        {
            if (row.IsNewRow)
                continue;
            var days = DayOrder.Where((_, i) => row.Cells[i].Value is true).ToArray();
            string? start = row.Cells[7].Value as string, end = row.Cells[8].Value as string;
            if (days.Length == 0 && string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))
                continue;
            if (days.Length == 0 || !TimeOnly.TryParseExact(start?.Trim(), "HH:mm", out var s) || !TimeOnly.TryParseExact(end?.Trim(), "HH:mm", out var e) || s == e)
            {
                error = $"Ligne {row.Index + 1} : cochez au moins un jour et indiquez un début et une fin différents au format HH:mm.";
                return false;
            }
            schedule.Add(new TimeWindow(days, s, e));
        }
        return true;
    }

    private async Task PairAsync()
    {
        try
        {
            host.Pair(pairingBox.Text);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, "Code d'association invalide.", "Association", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Async void handler: anything else (DPAPI, worker.json not writable) is reported rather than left unhandled.
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Association impossible :\n{ex.Message}", "Association", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        LoadSettings();
        await host.RestartAsync();
    }

    /// <summary>
    /// Runs a click handler that restarts the worker with its button disabled, so a double click cannot queue a second
    /// restart (WorkerHost serializes them anyway).
    /// </summary>
    private async Task WhileDisabledAsync(Func<Task> action)
    {
        pairButton.Enabled = redetectButton.Enabled = saveButton.Enabled = false;
        try
        {
            await action();
        }
        finally
        {
            pairButton.Enabled = redetectButton.Enabled = saveButton.Enabled = !host.Busy;
        }
    }

    // ---- Logs ----------------------------------------------------------------------------------

    private TabPage BuildLogsTab()
    {
        var page = new TabPage("Journaux") { Padding = new Padding(6) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var export = new Button { Text = "Exporter (7 derniers jours)…", AutoSize = true };
        var open = new Button { Text = "Ouvrir le dossier", AutoSize = true };
        export.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = "Archive zip|*.zip", FileName = $"emby-worker-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip" };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;
            Logger.Export(dlg.FileName);
            MessageBox.Show(this, "Journaux exportés (secrets masqués).", "Journaux", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", WorkerConfig.LogsDirectory) { UseShellExecute = true });
        bar.Controls.AddRange([export, open]);
        logBox.Lines = host.Logger.Tail();
        page.Controls.Add(logBox);
        page.Controls.Add(bar);
        return page;
    }

    /// <summary>Called on whatever thread logged the line.</summary>
    private void OnLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        BeginInvoke(() =>
        {
            // Keep the text box bounded: past ~400 KB start again from the logger's in-memory tail.
            if (logBox.TextLength > 400_000)
                logBox.Lines = host.Logger.Tail();
            else
                logBox.AppendText(line + Environment.NewLine);
        });
    }

    // ---- Layout helpers ------------------------------------------------------------------------

    private static NumericUpDown Percent() => new() { Minimum = 1, Maximum = 100, Width = 55 };

    private static Label Unit(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 3, 0) };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(760, 0) };

    private static FlowLayoutPanel Row(params Control[] controls) => Row(null, controls);

    private static FlowLayoutPanel Row(string? label, params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        if (label != null)
            row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        row.Controls.AddRange(controls);
        return row;
    }

    private static GroupBox Group(string title, params Control[] rows)
    {
        var box = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.AddRange(rows);
        box.Controls.Add(flow);
        return box;
    }
}
