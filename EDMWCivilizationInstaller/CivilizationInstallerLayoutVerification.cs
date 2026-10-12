using EDMWMapDesigner.Civilizations;

namespace EDMWCivilizationInstaller;

/// <summary>Checks control geometry offscreen without capturing pixels or invoking installer actions.</summary>
internal static class CivilizationInstallerLayoutVerification
{
    public static int Run(string gamePath)
    {
        try
        {
            var package = CivilizationInstallerPackageCodec.ReadExecutable(Environment.ProcessPath
                ?? throw new InvalidOperationException("The installer executable path is unavailable."));
            var cases = 0;
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                using var form = new CivilizationInstallerForm(package, gamePath, verificationMode: true)
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-30000, -30000)
                };
                form.Show();
                Application.DoEvents();

                // Probe scaled layout dimensions explicitly; this does not emulate monitor DPI messages.
                form.AutoScaleMode = AutoScaleMode.None;
                var defaultClient = form.ClientSize;
                form.Size = form.MinimumSize;
                var minimumClient = form.ClientSize;
                form.ClientSize = defaultClient;
                if (scale != 1f) form.Scale(new SizeF(scale, scale));

                var sizes = new[]
                {
                    (Name: "default", Client: Scaled(defaultClient, scale)),
                    (Name: "minimum", Client: Scaled(minimumClient, scale)),
                    (Name: "maximized-like", Client: Scaled(new Size(1920, 1080), scale))
                };
                foreach (var size in sizes)
                {
                    form.ClientSize = size.Client;
                    foreach (var recoveryVisible in new[] { false, true })
                    {
                        form.SetRecoveryVisibilityForVerification(recoveryVisible);
                        PerformLayouts(form);
                        Application.DoEvents();
                        PerformLayouts(form);
                        var context = $"{size.Name}; layout scale {scale:0.##}; recovery={recoveryVisible}; client={form.ClientSize}";
                        VerifyBounds(form, recoveryVisible, context);
                        Console.WriteLine("INSTALLER LAYOUT PASS: " + context);
                        cases++;
                    }
                }
                form.Hide();
            }
            Console.WriteLine($"INSTALLER LAYOUT VERIFICATION PASS: {cases} offscreen geometry cases. No installation or button action was performed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("INSTALLER LAYOUT VERIFICATION FAIL: " + error);
            return 1;
        }
    }

    private static Size Scaled(Size value, float scale) => new(
        (int)Math.Ceiling(value.Width * scale),
        (int)Math.Ceiling(value.Height * scale));

    private static void PerformLayouts(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) PerformLayouts(child);
    }

    private static void VerifyBounds(CivilizationInstallerForm form, bool recoveryVisible, string context)
    {
        var panel = form.ActionPanelForVerification;
        var buttons = form.ActionButtonsForVerification;
        Require(buttons.Count == 4, "Expected Install, Uninstall, Recover and Close actions.", context);
        Require(panel.Visible && panel.Width > 0 && panel.Height > 0, "Action panel is hidden or empty.", context);
        Require(buttons[2].Visible == recoveryVisible, "Recovery visibility was not applied.", context);

        var visible = buttons.Where(button => button.Visible).ToArray();
        Require(buttons[0].Visible && buttons[1].Visible && buttons[3].Visible,
            "Install, Uninstall or Close is hidden.", context);
        foreach (var button in visible)
        {
            var name = button.AccessibleName ?? button.Text;
            Require(ReferenceEquals(button.Parent, panel), name + " has an unexpected parent.", context);
            Require(button.Width > 0 && button.Height > 0, name + " has collapsed bounds: " + button.Bounds, context);
            Require(panel.ClientRectangle.Contains(button.Bounds),
                name + " is clipped by its action panel: " + button.Bounds, context);
            Require(form.ClientRectangle.Contains(BoundsInForm(form, button)),
                name + " is outside the form client area.", context);
        }
        for (var i = 0; i < visible.Length; i++)
            for (var j = i + 1; j < visible.Length; j++)
                Require(!visible[i].Bounds.IntersectsWith(visible[j].Bounds),
                    visible[i].Text + " overlaps " + visible[j].Text + ".", context);

        var actions = BoundsInForm(form, panel);
        var status = BoundsInForm(form, form.StatusForVerification);
        Require(form.ClientRectangle.Contains(actions), "Action panel is outside the form client area.", context);
        Require(actions.Top >= status.Bottom, "Action panel overlaps the validation status.", context);
    }

    private static Rectangle BoundsInForm(Form form, Control control)
    {
        var parent = control.Parent ?? throw new InvalidOperationException("A verified control has no parent.");
        return new Rectangle(form.PointToClient(parent.PointToScreen(control.Location)), control.Size);
    }

    private static void Require(bool condition, string message, string context)
    {
        if (!condition) throw new InvalidOperationException(message + " [" + context + "]");
    }
}
