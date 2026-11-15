using System.Runtime.InteropServices;

namespace Vitrines.App.Desktop;

/// <summary>
/// Совместимый слой Win32 для витрин: удержание внизу z-order, backdrop (Blur/Acrylic),
/// запрет активации, округлые углы и восстановление окна в рабочей области монитора.
/// Витрины остаются обычными top-level окнами (AllowsTransparency + WPF) — это надёжнее,
/// чем SetParent в WorkerW, который ломает отрисовку/ввод на части сборок Windows.
/// </summary>
internal static class DesktopLayerInterop
{
    public static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTop = new(0);

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoSendChanging = 0x0400;

    public static bool ShellPresent => FindWindow("Progman", null) != IntPtr.Zero;

    public static void ConfigureWindow(IntPtr hwnd, bool noActivate)
    {
        if (hwnd == IntPtr.Zero) return;
        SetNoActivate(hwnd, noActivate);
        TryEnableRoundedCorners(hwnd);
    }

    public static void SetNoActivate(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            var updated = enabled
                ? style | WsExNoActivate | WsExToolWindow
                : style & ~WsExNoActivate;
            if (updated == style) return;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(updated));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch { }
    }

    /// <summary>Опустить окно на самый низ z-order (слой рабочего стола) без активации.</summary>
    public static void PinToBottom(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero || !enabled) return;
        try
        {
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoSendChanging);
        }
        catch { }
    }

    /// <summary>Поднять в z-order (для отладки/временного показа поверх).</summary>
    public static void PinToTop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate); }
        catch { }
    }

    public static void ApplyBackdrop(IntPtr hwnd, bool useAcrylic, uint abgrTint)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (TryApplyAccent(hwnd, useAcrylic ? AccentState.AcrylicBlurBehind : AccentState.BlurBehind, abgrTint)) return;
            if (useAcrylic) TryApplyAccent(hwnd, AccentState.BlurBehind, abgrTint);
        }
        catch { }
    }

    public static void DisableBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { TryApplyAccent(hwnd, AccentState.Disabled, 0); } catch { }
    }

    private static bool TryApplyAccent(IntPtr hwnd, AccentState state, uint abgrTint)
    {
        var accent = new AccentPolicy
        {
            AccentState = (int)state,
            AccentFlags = state == AccentState.Disabled ? 0 : 2,
            GradientColor = unchecked((int)abgrTint),
            AnimationId = 0
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                SizeOfData = size,
                Data = ptr
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static void TryEnableRoundedCorners(IntPtr hwnd)
    {
        // Windows 11: DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
        if (Environment.OSVersion.Version.Build < 22000) return;
        try
        {
            var preference = 2;
            _ = DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Восстановить окно в видимой части ближайшего монитора (физические координаты Win32 —
    /// корректно работает при разном DPI мониторов). Меняет размер, только если он больше рабочей области.
    /// </summary>
    public static void EnsureVisibleOnMonitor(IntPtr hwnd, int margin = 24)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var work = info.rcWork;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        var maxWidth = Math.Max(220, work.Right - work.Left - margin * 2);
        var maxHeight = Math.Max(160, work.Bottom - work.Top - margin * 2);
        width = Math.Min(width, maxWidth);
        height = Math.Min(height, maxHeight);

        // Если окно почти целиком вне монитора — притягиваем к рабочей области.
        var visibleW = Math.Min(rect.Right, work.Right) - Math.Max(rect.Left, work.Left);
        var visibleH = Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Top, work.Top);
        var x = rect.Left;
        var y = rect.Top;
        if (visibleW < 80 || visibleH < 60)
        {
            x = work.Left + Math.Max(margin, 40);
            y = work.Top + Math.Max(margin, 40);
        }

        x = Math.Clamp(x, work.Left - 8, Math.Max(work.Left - 8, work.Right - width + 8));
        y = Math.Clamp(y, work.Top - 8, Math.Max(work.Top - 8, work.Bottom - height + 8));

        if (x == rect.Left && y == rect.Top && width == rect.Right - rect.Left && height == rect.Bottom - rect.Top)
            return;

        try { SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate); }
        catch { }
    }

    /// <summary>Ищет WorkerW оболочки (диагностика/перепривязка после рестарта explorer).</summary>
    public static IntPtr FindDesktopWorkerW()
    {
        try
        {
            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1),
                SendMessageTimeoutFlags.Normal, 1000, out _);
            var result = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    result = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }
        catch { return IntPtr.Zero; }
    }

    private enum AccentState
    {
        Disabled = 0,
        BlurBehind = 3,
        AcrylicBlurBehind = 4
    }

    private const int WcaAccentPolicy = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        Normal = 0
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        SendMessageTimeoutFlags flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
}
