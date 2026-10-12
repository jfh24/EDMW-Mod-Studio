using System.Runtime.InteropServices;

namespace EDMWMapDesigner.UI;

internal static class MedievalWindowChrome
{
    private const int UseImmersiveDarkMode = 20;
    private const int NonClientLeftButtonDown = 0x00A1;
    private const int CaptionHit = 2;
    private const int LeftHit = 10;
    private const int RightHit = 11;
    private const int TopHit = 12;
    private const int TopLeftHit = 13;
    private const int TopRightHit = 14;
    private const int BottomHit = 15;
    private const int BottomLeftHit = 16;
    private const int BottomRightHit = 17;

    public const int NonClientHitTestMessage = 0x0084;
    public const int ClientHit = 1;

    public static void Apply(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var enabled = 1;
        _ = DwmSetWindowAttribute(form.Handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    public static void ApplyDarkControlTheme(Control control)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !control.IsHandleCreated) return;
        _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }

    public static void BeginDrag(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = ReleaseCapture();
        _ = SendMessage(form.Handle, NonClientLeftButtonDown, CaptionHit, 0);
    }

    public static bool TryGetResizeHit(Form form, Point point, out int hit)
    {
        var grip = Math.Max(6, (int)Math.Ceiling(6f * form.DeviceDpi / 96f));
        var left = point.X < grip;
        var right = point.X >= form.ClientSize.Width - grip;
        var top = point.Y < grip;
        var bottom = point.Y >= form.ClientSize.Height - grip;

        hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => TopLeftHit,
            (_, true, true, _) => TopRightHit,
            (true, _, _, true) => BottomLeftHit,
            (_, true, _, true) => BottomRightHit,
            (true, _, _, _) => LeftHit,
            (_, true, _, _) => RightHit,
            (_, _, true, _) => TopHit,
            (_, _, _, true) => BottomHit,
            _ => ClientHit
        };
        return hit != ClientHit;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute,
        ref int attributeValue, int attributeSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, int wordParameter, int longParameter);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr windowHandle, string? subAppName, string? subIdList);
}
