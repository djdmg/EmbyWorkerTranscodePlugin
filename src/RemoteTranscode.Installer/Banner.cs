namespace RemoteTranscode.Installer;

/// <summary>Header of the setup windows: the worker's logo (Assets\worker.ico, embedded), its name and a subtitle.</summary>
internal static class Banner
{
    public static Panel Create(string subtitle)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.White, Padding = new Padding(12, 10, 12, 10) };
        var logo = new PictureBox { Size = new Size(56, 56), Location = new Point(14, 10), SizeMode = PictureBoxSizeMode.Zoom };
        try
        {
            using var stream = typeof(Banner).Assembly.GetManifestResourceStream("worker.ico");
            if (stream != null)
                logo.Image = new Icon(stream, 64, 64).ToBitmap();
        }
        catch (ArgumentException) { }
        var title = new Label
        {
            AutoSize = true,
            Location = new Point(82, 12),
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 14, FontStyle.Bold),
            Text = Setup.AppName,
        };
        var sub = new Label { AutoSize = true, Location = new Point(84, 44), ForeColor = Color.DimGray, Text = subtitle };
        var line = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.Gainsboro };
        panel.Controls.AddRange([logo, title, sub, line]);
        return panel;
    }
}
