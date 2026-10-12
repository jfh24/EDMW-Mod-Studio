using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Offscreen native-message diagnostics. This does not capture or inspect rendered pixels.</summary>
internal static class CivilizationRepaintVerification
{
    private const int IdleMilliseconds = 1000;
    private const int MaximumIdleNativePaints = 100;

    public static int Run(string gamePath, string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var reports = new List<ProbeReport>();
            foreach (var composited in new[] { false, true }) reports.Add(Measure(gamePath, composited));
            var reportPath = Path.Combine(outputDirectory, "civilization-repaint.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var report in reports)
            {
                Console.WriteLine($"REPAINT {report.ParentStyle}: {report.IdleNativePaints} idle native paints; " +
                    $"{report.IdleInvalidations} invalidations; {report.IdleLayouts} layouts; " +
                    $"{report.IdleSelectionEvents} selection/dropdown events; " +
                    $"{report.SuppressedPaints} storm messages suppressed; document retained={report.DocumentRetained}.");
            }
            var passed = reports.All(report => report.DocumentRetained && report.InteractionSelectionEvents >= 2 &&
                report.IdleSelectionEvents == 0 && report.SuppressedPaints == 0 &&
                report.Controls.Where(control => control.NativeInput).All(control =>
                    control.Paint + control.PrintClient <= MaximumIdleNativePaints));
            Console.WriteLine(passed ? "CIVILIZATION REPAINT VERIFICATION PASS" :
                $"CIVILIZATION REPAINT VERIFICATION FAIL (native input idle limit {MaximumIdleNativePaints}/second; see {reportPath})");
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("CIVILIZATION REPAINT VERIFICATION FAIL: " + exception);
            return 1;
        }
    }

    private static ProbeReport Measure(string gamePath, bool composited)
    {
        using var form = new ProbeForm(composited)
        {
            Text = "Civilization repaint diagnostics",
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
            Size = new Size(1300, 850),
            BackColor = MedievalTheme.Shell
        };
        using var view = new CivilizationEditorView(() => gamePath) { Dock = DockStyle.Fill };
        var probeRow = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(12, 4, 12, 4) };
        var combo = new MedievalComboBox
        {
            Dock = DockStyle.Left, Width = 240, DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Repaint diagnostic dropdown"
        };
        combo.Items.AddRange(new object[] { "Diagnostic selection A", "Diagnostic selection B" });
        combo.SelectedIndex = 0;
        MedievalTheme.StyleTextInput(combo);
        probeRow.Controls.Add(combo);
        form.Controls.Add(view);
        form.Controls.Add(probeRow);
        // Attach before showing handles so a pre-fix paint loop is observed and bounded.
        using var trace = new PaintTrace(form);
        view.LoadGameCatalog();
        var civilization = view.SnapshotProject.Civilizations.Single(civ => civ.Name == "France");
        view.SelectCivilization(civilization.Id);
        view.SelectCategory(CivilizationCategory.Units);
        form.Show();
        PumpFor(250);
        var before = view.SnapshotProject.Serialize();

        trace.ResetMeasurement();
        PumpFor(IdleMilliseconds);
        var idle = trace.ReadControls();
        var idleSelectionEvents = idle.Sum(control => control.SelectionEvents + control.DropdownEvents);
        var idleNativePaints = idle.Where(control => control.NativeInput).Sum(control => control.Paint + control.PrintClient);

        trace.ResetMeasurement();
        combo.SelectedIndex = 1;
        combo.SelectedIndex = 0;
        view.SelectCategory(CivilizationCategory.Buildings);
        view.SelectCategory(CivilizationCategory.Technologies);
        view.SelectCategory(CivilizationCategory.Units);
        PumpFor(250);
        var interactionSelectionEvents = trace.ReadControls().Sum(control => control.SelectionEvents);
        var retained = before == view.SnapshotProject.Serialize();
        form.Hide();
        return new ProbeReport(composited ? "WS_EX_COMPOSITED" : "ordinary", IdleMilliseconds,
            idleNativePaints, idle.Sum(control => control.Invalidations), idle.Sum(control => control.Layouts),
            idleSelectionEvents, interactionSelectionEvents, trace.SuppressedPaints, retained, idle);
    }

    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }

    private sealed class ProbeForm(bool composited) : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // Tool window; no taskbar / activation side effects.
                if (composited) parameters.ExStyle |= 0x02000000;
                else parameters.ExStyle &= ~0x02000000;
                return parameters;
            }
        }
    }

    private sealed class PaintTrace : IDisposable
    {
        private readonly Dictionary<Control, Counter> _controls = new(ReferenceEqualityComparer.Instance);
        private bool _disposed;
        public int SuppressedPaints { get; private set; }

        public PaintTrace(Control root) => Register(root);

        private void Register(Control control)
        {
            if (_disposed || _controls.ContainsKey(control)) return;
            var counter = new Counter(control);
            var window = new PaintWindow(counter, () => SuppressedPaints++);
            counter.Window = window;
            _controls.Add(control, counter);
            control.Layout += (_, _) => counter.Layouts++;
            control.Invalidated += (_, _) => counter.Invalidations++;
            control.ControlAdded += (_, args) => Register(args.Control!);
            control.HandleCreated += (_, _) => { if (!_disposed) window.Attach(control.Handle); };
            control.HandleDestroyed += (_, _) => window.Detach();
            if (control is ListBox list) list.SelectedIndexChanged += (_, _) => counter.SelectionEvents++;
            if (control is ComboBox combo)
            {
                combo.SelectedIndexChanged += (_, _) => counter.SelectionEvents++;
                combo.DropDown += (_, _) => counter.DropdownEvents++;
                combo.DropDownClosed += (_, _) => counter.DropdownEvents++;
            }
            if (control.IsHandleCreated) window.Attach(control.Handle);
            foreach (Control child in control.Controls) Register(child);
        }

        public void ResetMeasurement()
        {
            foreach (var counter in _controls.Values) counter.Reset();
        }

        public IReadOnlyList<ControlReport> ReadControls() => _controls.Values
            .Where(counter => !counter.Control.IsDisposed)
            .Select(counter => new ControlReport(counter.Control.GetType().Name,
                counter.Control.AccessibleName ?? counter.Control.Name,
                counter.Control is ComboBox or ListBox,
                counter.Paint, counter.PrintClient, counter.EraseBackground,
                counter.Layouts, counter.Invalidations, counter.SelectionEvents, counter.DropdownEvents))
            .OrderByDescending(counter => counter.Paint + counter.PrintClient)
            .ThenBy(counter => counter.Name, StringComparer.Ordinal).ToArray();

        public void Dispose()
        {
            _disposed = true;
            foreach (var counter in _controls.Values) counter.Window?.Detach();
        }
    }

    private sealed class Counter(Control control)
    {
        public Control Control { get; } = control;
        public PaintWindow? Window { get; set; }
        public int Paint;
        public int PrintClient;
        public int EraseBackground;
        public int Layouts;
        public int Invalidations;
        public int SelectionEvents;
        public int DropdownEvents;
        public void Reset() => Paint = PrintClient = EraseBackground = Layouts = Invalidations = SelectionEvents = DropdownEvents = 0;
    }

    private sealed class PaintWindow(Counter counter, Action suppressed) : NativeWindow
    {
        private const int WmPaint = 0x000F;
        private const int WmEraseBackground = 0x0014;
        private const int WmPrintClient = 0x0318;
        private long _burstStarted = Stopwatch.GetTimestamp();
        private int _burstPaints;

        public void Attach(IntPtr handle)
        {
            if (Handle == handle) return;
            Detach();
            AssignHandle(handle);
        }

        public void Detach() { if (Handle != IntPtr.Zero) ReleaseHandle(); }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmPaint) counter.Paint++;
            else if (message.Msg == WmPrintClient) counter.PrintClient++;
            else if (message.Msg == WmEraseBackground) counter.EraseBackground++;
            if (message.Msg is WmPaint or WmPrintClient)
            {
                if (Stopwatch.GetElapsedTime(_burstStarted).TotalMilliseconds >= 1000)
                {
                    _burstStarted = Stopwatch.GetTimestamp();
                    _burstPaints = 0;
                }
                // Keep diagnostics bounded even if the old native painting path
                // continually regenerates WM_PAINT while DoEvents drains the queue.
                if (++_burstPaints > 500)
                {
                    if (message.Msg == WmPaint) ValidateRect(message.HWnd, IntPtr.Zero);
                    suppressed();
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref message);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ValidateRect(IntPtr window, IntPtr rectangle);
    }

    private sealed record ControlReport(string Type, string Name, bool NativeInput,
        int Paint, int PrintClient, int EraseBackground, int Layouts, int Invalidations,
        int SelectionEvents, int DropdownEvents);
    private sealed record ProbeReport(string ParentStyle, int IdleMilliseconds,
        int IdleNativePaints, int IdleInvalidations, int IdleLayouts, int IdleSelectionEvents,
        int InteractionSelectionEvents, int SuppressedPaints, bool DocumentRetained,
        IReadOnlyList<ControlReport> Controls);
}
