namespace RemoteTranscode.Installer;

/// <summary>Single-page wizard: options, then progress and result in the same window.</summary>
internal sealed class SetupForm : Form
{
    private readonly InstallOptions defaults;
    private readonly TextBox folderBox = new() { Dock = DockStyle.Fill };
    private readonly Button browseButton = new() { Text = "Parcourir…", AutoSize = true };
    private readonly CheckBox startupBox = new() { Text = "Démarrer avec Windows", AutoSize = true };
    private readonly CheckBox desktopBox = new() { Text = "Créer un raccourci sur le Bureau", AutoSize = true };
    private readonly CheckBox launchBox = new() { Text = "Lancer Emby Remote Worker à la fin", AutoSize = true };
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Height = 18 };
    private readonly Label statusLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "" };
    private readonly Button installButton = new() { Text = "Installer", AutoSize = true, MinimumSize = new Size(90, 0) };
    private readonly Button cancelButton = new() { Text = "Annuler", AutoSize = true, MinimumSize = new Size(90, 0) };
    private bool busy;

    public SetupForm(InstallOptions options)
    {
        defaults = options;
        Text = $"Installation de {Setup.AppName} {Setup.Version}";
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont ?? Font;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(620, 636);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (ArgumentException) { }

        folderBox.Text = options.Folder;
        startupBox.Checked = options.StartWithWindows;
        launchBox.Checked = options.Launch;

        var welcome = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(590, 0),
            Text = $"Bienvenue dans l'installation de {Setup.AppName}.\n\n" +
                   "Ce programme met le PC à disposition de votre serveur Emby pour le transcodage. " +
                   "L'installation se fait pour l'utilisateur courant uniquement et ne demande pas de droits d'administrateur.\n\n" +
                   "FFmpeg est inclus (dossier ffmpeg\\). Il s'agit d'un programme distinct distribué sous licence " +
                   "GNU GPL version 3, reproduite ci-dessous ; les sources correspondantes sont indiquées dans ffmpeg\\SOURCES.txt.",
        };
        var license = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
            Text = (Setup.ReadFfmpegLicense() ?? "(licence introuvable dans le programme d'installation)").ReplaceLineEndings("\r\n"),
        };

        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(folderBox, 0, 0);
        folderRow.Controls.Add(browseButton, 1, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(installButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // welcome
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // licence
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // folder label
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // folder row
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // startup
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // desktop
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // launch
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // progress
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // buttons
        layout.Controls.Add(welcome);
        layout.Controls.Add(license);
        layout.Controls.Add(new Label { Text = "Dossier d'installation :", AutoSize = true, Margin = new Padding(3, 10, 3, 0) });
        layout.Controls.Add(folderRow);
        layout.Controls.Add(startupBox);
        layout.Controls.Add(desktopBox);
        layout.Controls.Add(launchBox);
        layout.Controls.Add(progressBar);
        layout.Controls.Add(statusLabel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        Controls.Add(Banner.Create($"Installation de la version {Setup.Version}"));

        AcceptButton = installButton;
        CancelButton = cancelButton;
        browseButton.Click += (_, _) => Browse();
        installButton.Click += async (_, _) => await InstallAsync();
        cancelButton.Click += (_, _) => Close();
        FormClosing += (_, e) => e.Cancel = busy;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choisir le dossier d'installation",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        string current = folderBox.Text.Trim();
        dialog.InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current) ?? "";
        if (dialog.ShowDialog(this) == DialogResult.OK)
            // Picking a parent folder (e.g. "D:\Apps") would otherwise spread the files into it.
            folderBox.Text = string.Equals(Path.GetFileName(dialog.SelectedPath), "EmbyRemoteWorker", StringComparison.OrdinalIgnoreCase)
                ? dialog.SelectedPath
                : Path.Combine(dialog.SelectedPath, "EmbyRemoteWorker");
    }

    private async Task InstallAsync()
    {
        string folder;
        try
        {
            folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folderBox.Text.Trim()));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, "Dossier d'installation invalide.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // No warning for a non-empty folder any more: Setup.Install then uses an EmbyRemoteWorker subfolder, and the
        // uninstall only deletes the files listed in its manifest.

        var options = defaults with
        {
            Folder = folder,
            StartWithWindows = startupBox.Checked,
            DesktopShortcut = desktopBox.Checked,
            Launch = launchBox.Checked,
        };
        busy = true;
        foreach (Control c in new Control[] { folderBox, browseButton, startupBox, desktopBox, launchBox, installButton, cancelButton })
            c.Enabled = false;
        var progress = new Progress<(int Percent, string Text)>(p =>
        {
            progressBar.Value = Math.Clamp(p.Percent, 0, 100);
            statusLabel.Text = p.Text;
        });
        try
        {
            string installed = await Task.Run(() => Setup.Install(options, progress));
            statusLabel.Text = $"{Setup.AppName} est installé dans {installed}.";
            progressBar.Value = 100;
            installButton.Visible = false;
        }
        catch (Exception e)
        {
            statusLabel.Text = "Échec de l'installation.";
            MessageBox.Show(this, "L'installation a échoué :\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            installButton.Enabled = true;
            foreach (Control c in new Control[] { folderBox, browseButton, startupBox, desktopBox, launchBox })
                c.Enabled = true;
        }
        finally
        {
            busy = false;
            cancelButton.Text = "Fermer";
            cancelButton.Enabled = true;
        }
    }
}
