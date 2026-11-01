namespace RemoteTranscode.Installer;

/// <summary>
/// Update of an existing installation: one button, same folder and options, configuration and pairing untouched,
/// the worker closed during the copy and started again afterwards.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly InstallOptions options;
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Height = 18 };
    private readonly Label statusLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "" };
    private readonly Button updateButton = new() { Text = "Mettre à jour", AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly Button cancelButton = new() { Text = "Annuler", AutoSize = true, MinimumSize = new Size(90, 0) };
    private bool busy;

    public UpdateForm(InstallOptions options, Setup.ExistingInstall existing)
    {
        this.options = options;
        Text = $"Mise à jour de {Setup.AppName}";
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont ?? Font;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(520, 326);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (ArgumentException) { }

        string installed = existing.Version ?? "inconnue";
        bool same = string.Equals(installed, Setup.Version, StringComparison.OrdinalIgnoreCase);
        var info = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(490, 0),
            Text = (same ? $"{Setup.AppName} {installed} est déjà installé ; il sera réinstallé.\n\n"
                         : $"Version installée : {installed}\nNouvelle version : {Setup.Version}\n\n") +
                   $"Dossier : {existing.Folder}\n\n" +
                   "La configuration, l'association au serveur et les réglages sont conservés." +
                   (existing.Running ? " Le worker est fermé pendant la copie puis relancé ; une tâche en cours sur ce PC reprend sur le serveur ou un autre worker." : ""),
        };
        updateButton.Text = same ? "Réinstaller" : "Mettre à jour";

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(updateButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // information
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // progress
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // buttons
        layout.Controls.Add(info);
        layout.Controls.Add(progressBar);
        layout.Controls.Add(statusLabel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        Controls.Add(Banner.Create($"Mise à jour vers la version {Setup.Version}"));

        AcceptButton = updateButton;
        CancelButton = cancelButton;
        updateButton.Click += async (_, _) => await UpdateAsync();
        cancelButton.Click += (_, _) => Close();
        FormClosing += (_, e) => e.Cancel = busy;
    }

    private async Task UpdateAsync()
    {
        busy = true;
        updateButton.Enabled = cancelButton.Enabled = false;
        var progress = new Progress<(int Percent, string Text)>(p =>
        {
            progressBar.Value = Math.Clamp(p.Percent, 0, 100);
            statusLabel.Text = p.Text;
        });
        try
        {
            await Task.Run(() => Setup.Install(options, progress));
            statusLabel.Text = $"{Setup.AppName} {Setup.Version} est installé.";
            progressBar.Value = 100;
            updateButton.Visible = false;
        }
        catch (Exception e)
        {
            statusLabel.Text = "Échec de la mise à jour.";
            MessageBox.Show(this, "La mise à jour a échoué :\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            updateButton.Enabled = true;
        }
        finally
        {
            busy = false;
            cancelButton.Text = "Fermer";
            cancelButton.Enabled = true;
        }
    }
}
