using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Vitrines.Core.Models;
using Vitrines.Core.Storage;

namespace Vitrines.App.Desktop;

public partial class VitrineWindow : Window
{
    internal const string InternalDragFormat = "Vitrines.InternalItem";

    private readonly Vitrine _model;
    private readonly IDesktopActions _actions;
    private readonly DispatcherTimer _saveDebounce;
    private bool _suppressSave;
    private bool _forceClose;
    private Point _dragStart;
    private IntPtr _workerw;

    public ObservableCollection<VitrineItemView> Items { get; } = new();

    public VitrineWindow(Vitrine model, IDesktopActions actions)
    {
        InitializeComponent();
        _model = model;
        _actions = actions;
        ItemsPanel.ItemsSource = Items;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += async (_, _) => { _saveDebounce.Stop(); await _actions.SaveLayoutAsync(); };

        Loaded += OnLoaded;
        Deactivated += (_, _) => SendToBack();
        Closing += OnClosing;
        SourceInitialized += (_, _) => AttachToDesktopLayer();

        ApplyModel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachToDesktopLayer();
        SendToBack();
        ApplyAppearance(true);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose) return;
        // по умолчанию скрываем, а не уничтожаем насовсем? Manager управляет жизненным циклом.
    }

    public void ForceClose() { _forceClose = true; try { Close(); } catch { } }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
        AttachToDesktopLayer();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const int WM_DISPLAYCHANGE = 0x007E;
        if (msg == WM_WINDOWPOSCHANGING) { /* allow */ }
        if (msg == WM_DISPLAYCHANGE) Dispatcher.InvokeAsync(() => { AttachToDesktopLayer(); SendToBack(); });
        // TaskbarCreated (shell restart) - reattach
        if (msg == _taskbarCreated && _taskbarCreated != 0) Dispatcher.InvokeAsync(() => { AttachToDesktopLayer(); SendToBack(); });
        return IntPtr.Zero;
    }

    private static readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private void AttachToDesktopLayer()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            _workerw = GetDesktopWorkerW();
            if (_workerw != IntPtr.Zero)
            {
                // делаем дочерним WorkerW, чтобы быть на слое рабочего стола и переживать некоторые сценарии
                SetParent(hwnd, _workerw);
            }
            SendToBack();
            TryEnableBlur(hwnd);
        }
        catch { SendToBack(); }
    }

    private void SendToBack()
    {
        if (!IsLoaded) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        TryEnableBlur(hwnd);
    }

    private static IntPtr GetDesktopWorkerW()
    {
        var progman = FindWindow("Progman", null);
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SendMessageTimeoutFlags.SMTO_NORMAL, 1000, out _);
        IntPtr workerw = IntPtr.Zero;
        EnumWindows((top, lparam) =>
        {
            var shell = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shell != IntPtr.Zero)
            {
                workerw = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);
        return workerw;
    }

    private void TryEnableBlur(IntPtr hwnd)
    {
        try
        {
            var accent = new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND };
            var size = Marshal.SizeOf(accent);
            var pAccent = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pAccent, false);
                var data = new WindowCompositionAttributeData { Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY, SizeOfData = size, Data = pAccent };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(pAccent); }
        }
        catch { }
    }

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, SendMessageTimeoutFlags flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string lpString);
    [Flags] private enum SendMessageTimeoutFlags : uint { SMTO_NORMAL = 0x0 }

    private enum AccentState { ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3 }
    private enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public AccentState AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowCompositionAttributeData { public WindowCompositionAttribute Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    public Guid VitrineId => _model.Id;

    public void ApplyModel()
    {
        _suppressSave = true;
        TitleText.Text = _model.Name;
        Left = _model.X; Top = _model.Y; Width = _model.Width; Height = _model.Height;
        var op = _model.Style?.Opacity ?? 0.9;
        if (op < 0.35) op = 0.35; if (op > 1.0) op = 1.0;
        RootBorder.Opacity = op;
        ApplyAppearance(false);
        RefreshItems();
        _suppressSave = false;
        SendToBack();
    }

    private void ApplyAppearance(bool animate)
    {
        var a = _actions.Data.Watch.Appearance ?? new AppearanceSettings();
        // акцент
        try
        {
            var accent = (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(a.Accent) ? "#7C9CFF" : a.Accent);
            Resources["VAccent"] = new SolidColorBrush(accent);
            if (FindName("KindBadge") is TextBlock == false) { }
        }
        catch { }
        // размер плиток / плотность
        var tile = a.TileSize <= 0 ? 92 : a.TileSize;
        if (string.Equals(a.Density, "compact", StringComparison.OrdinalIgnoreCase)) tile = (int)(tile * 0.82);
        if (string.Equals(a.Density, "comfortable", StringComparison.OrdinalIgnoreCase)) tile = (int)(tile * 1.15);
        Resources["TileWidth"] = (double)Math.Max(72, Math.Min(160, tile));
        Resources["FontScale"] = a.FontScale <= 0 ? 1.0 : a.FontScale;
        if (animate && a.Animations && IsLoaded)
        {
            var fade = new DoubleAnimation(0.0, RootBorder.Opacity, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            RootBorder.BeginAnimation(OpacityProperty, fade);
        }
    }

    public void RefreshItems()
    {
        Items.Clear();
        if (_model.Kind == VitrineKind.Portal)
            foreach (var it in EnumeratePortalItems()) Items.Add(VitrineItemView.From(it, _model.Id, true, _actions.Data.Watch.Appearance));
        else
            foreach (var it in _model.Items) Items.Add(VitrineItemView.From(it, _model.Id, false, _actions.Data.Watch.Appearance));
        EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        KindBadge.Text = _model.Kind == VitrineKind.Portal ? $"портал · {Items.Count}" : $"витрина · {Items.Count}";
    }

    private IEnumerable<VitrineItem> EnumeratePortalItems()
    {
        if (string.IsNullOrWhiteSpace(_model.FolderPath) || !Directory.Exists(_model.FolderPath))
            return Enumerable.Empty<VitrineItem>();
        try
        {
            return Directory.EnumerateFileSystemEntries(_model.FolderPath).Select(p => new VitrineItem
            { Path = p, DisplayName = Path.GetFileName(p), Kind = DetectKind(p) });
        }
        catch { return Enumerable.Empty<VitrineItem>(); }
    }

    private static ItemKind DetectKind(string path)
    {
        if (Directory.Exists(path)) return ItemKind.Folder;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lnk" || ext == ".url") return ItemKind.Shortcut;
        if (ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".appref-ms") return ItemKind.Application;
        return ItemKind.File;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_model.Kind == VitrineKind.Portal && !string.IsNullOrWhiteSpace(_model.FolderPath) && Directory.Exists(_model.FolderPath)) TryOpen(_model.FolderPath);
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is FrameworkElement fe && fe.DataContext is VitrineItemView v) TryOpen(v.Path);
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = default; return; }
        if (sender is not FrameworkElement fe || fe.DataContext is not VitrineItemView v) return;
        if (_model.Kind == VitrineKind.Portal || v.IsPortalItem) return;
        var pos = e.GetPosition(this);
        if (_dragStart == default) { _dragStart = pos; return; }
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = default;
        var data = new DataObject();
        data.SetData(InternalDragFormat, new InternalDragData { SourceVitrineId = _model.Id, ItemPath = v.Path });
        DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
    }

    private static void TryOpen(string path)
    { try { if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }

    private static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else { var dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        }
        catch { }
    }

    private void Window_LocationChanged(object? sender, EventArgs e) => ScheduleSaveBounds();
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleSaveBounds();
    private void ScheduleSaveBounds()
    {
        if (_suppressSave) return;
        _model.X = Left; _model.Y = Top; _model.Width = Width; _model.Height = Height;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent(InternalDragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(InternalDragFormat) is InternalDragData drag) { await HandleInternalDropAsync(drag); e.Handled = true; return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (_model.Kind == VitrineKind.Portal)
            {
                if (!string.IsNullOrWhiteSpace(_model.FolderPath) && (File.Exists(p) || Directory.Exists(p)))
                    await _actions.MoveFileToFolderAsync(p, _model.FolderPath);
            }
            else
            {
                var pathToAdd = p;
                if (File.Exists(p) || Directory.Exists(p))
                {
                    var storage = VitrineStorage.GetManagedFolder(_model);
                    if (!VitrineStorage.IsInDirectory(p, storage))
                    {
                        var moved = await _actions.MoveFileToFolderAsync(p, storage);
                        if (moved != null) pathToAdd = moved;
                    }
                }
                if (_model.Items.Any(i => string.Equals(i.Path, pathToAdd, StringComparison.OrdinalIgnoreCase))) continue;
                _model.Items.Add(new VitrineItem { Path = pathToAdd, DisplayName = Path.GetFileName(pathToAdd), Kind = DetectKind(pathToAdd), IsPinned = true });
            }
        }
        RefreshItems();
        await _actions.SaveLayoutAsync();
        e.Handled = true;
    }

    private async Task HandleInternalDropAsync(InternalDragData drag)
    {
        if (drag.SourceVitrineId == _model.Id) return;
        var source = _actions.Data.Vitrines.FirstOrDefault(v => v.Id == drag.SourceVitrineId);
        if (source == null) return;
        var item = source.Items.FirstOrDefault(i => string.Equals(i.Path, drag.ItemPath, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;

        if (_model.Kind == VitrineKind.Portal)
        {
            if (string.IsNullOrWhiteSpace(_model.FolderPath)) return;
            var final = await _actions.MoveFileToFolderAsync(item.Path, _model.FolderPath);
            if (final == null) return;
            source.Items.Remove(item);
            await _actions.SaveLayoutAsync();
            RefreshItems();
            _actions.LogManual($"Элемент перемещён в папку портала: {item.DisplayName} → {_model.Name}");
            return;
        }

        source.Items.Remove(item);
        item.IsPinned = true;
        var storage = VitrineStorage.GetManagedFolder(_model);
        if ((File.Exists(item.Path) || Directory.Exists(item.Path)) && !VitrineStorage.IsInDirectory(item.Path, storage))
        {
            var moved = await _actions.MoveFileToFolderAsync(item.Path, storage);
            if (moved != null) { item.Path = moved; item.DisplayName = Path.GetFileName(moved); }
        }
        if (!_model.Items.Any(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            _model.Items.Add(item);
        RefreshItems();
        await _actions.SaveLayoutAsync();
        _actions.LogManual($"Элемент перенесён: {item.DisplayName} → {_model.Name}");
    }

    private async void RenameVitrine_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Новое имя витрины:", _model.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _model.Name = name.Trim();
        ApplyModel();
        await _actions.SaveLayoutAsync();
    }

    private async void MakePortal_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Выберите папку, которую будет показывать портал" };
        if (dlg.ShowDialog(this) != true) return;
        _model.Kind = VitrineKind.Portal;
        _model.FolderPath = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(_model.Name) || _model.Name == "Новая витрина")
            _model.Name = Path.GetFileName(dlg.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _model.Items.Clear();
        ApplyModel();
        await _actions.SaveLayoutAsync();
        _actions.LogManual($"Витрина настроена как портал: {_model.Name}");
    }

    private async void MakeCollection_Click(object sender, RoutedEventArgs e)
    {
        _model.Kind = VitrineKind.Collection;
        _model.FolderPath = null;
        ApplyModel();
        await _actions.SaveLayoutAsync();
        _actions.LogManual($"Витрина переведена в режим коллекции: {_model.Name}");
    }

    private void OpenPortalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Kind == VitrineKind.Portal && !string.IsNullOrWhiteSpace(_model.FolderPath)) TryOpen(_model.FolderPath);
        else TryOpen(VitrineStorage.GetManagedFolder(_model));
    }

    private async void OpacityUp_Click(object sender, RoutedEventArgs e)
    { _model.Style ??= new VitrineStyle(); _model.Style.Opacity = Math.Min(1.0, (_model.Style.Opacity <= 0 ? 0.9 : _model.Style.Opacity) + 0.08); ApplyModel(); await _actions.SaveLayoutAsync(); }
    private async void OpacityDown_Click(object sender, RoutedEventArgs e)
    { _model.Style ??= new VitrineStyle(); _model.Style.Opacity = Math.Max(0.35, (_model.Style.Opacity <= 0 ? 0.9 : _model.Style.Opacity) - 0.08); ApplyModel(); await _actions.SaveLayoutAsync(); }

    private async void DeleteVitrine_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(this, $"Удалить витрину «{_model.Name}»? Элементы не удаляются с диска, из коллекции уберутся ссылки.", "Удаление витрины", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;
        _actions.Data.Vitrines.RemoveAll(v => v.Id == _model.Id);
        await _actions.SaveLayoutAsync();
        ForceClose();
    }

    private VitrineItemView? MenuItemView(object sender)
    {
        if (sender is FrameworkElement fe && fe.DataContext is VitrineItemView v1) return v1;
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target) return target.DataContext as VitrineItemView;
        return null;
    }

    private void ItemMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm || cm.PlacementTarget is not FrameworkElement fe || fe.DataContext is not VitrineItemView v) return;
        var isCollectionItem = !v.IsPortalItem && _model.Kind == VitrineKind.Collection;
        foreach (var obj in cm.Items)
            if (obj is MenuItem mi && (mi.Header?.ToString()?.Contains("Закрепить") == true || mi.Header?.ToString()?.Contains("Убрать") == true))
                mi.IsEnabled = isCollectionItem;
    }

    private void ItemOpen_Click(object sender, RoutedEventArgs e) { var v = MenuItemView(sender); if (v != null) TryOpen(v.Path); }
    private void ItemShowInFolder_Click(object sender, RoutedEventArgs e) { var v = MenuItemView(sender); if (v != null) ShowInFolder(v.Path); }
    private async void ItemPin_Click(object sender, RoutedEventArgs e)
    { var v = MenuItemView(sender); if (v == null || v.IsPortalItem) return; var it = _model.Items.FirstOrDefault(i => string.Equals(i.Path, v.Path, StringComparison.OrdinalIgnoreCase)); if (it == null) return; it.IsPinned = !it.IsPinned; RefreshItems(); await _actions.SaveLayoutAsync(); }
    private async void ItemRemove_Click(object sender, RoutedEventArgs e)
    { var v = MenuItemView(sender); if (v == null || v.IsPortalItem) return; _model.Items.RemoveAll(i => string.Equals(i.Path, v.Path, StringComparison.OrdinalIgnoreCase)); RefreshItems(); await _actions.SaveLayoutAsync(); }

    private static string? Prompt(string text, string def)
    {
        var w = new Window { Title = "Витрины", Width = 360, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, Content = new StackPanel { Margin = new Thickness(12) } };
        var sp = (StackPanel)w.Content; sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Text = def, Margin = new Thickness(0, 0, 0, 10) }; sp.Children.Add(tb);
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { w.DialogResult = true; w.Close(); }; sp.Children.Add(ok);
        w.Loaded += (_, _) => { tb.SelectAll(); tb.Focus(); };
        return w.ShowDialog() == true ? tb.Text : null;
    }

    private sealed class InternalDragData { public Guid SourceVitrineId { get; init; } public string ItemPath { get; init; } = string.Empty; }
}

public sealed class VitrineItemView
{
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string KindGlyph { get; init; } = "▢";
    public string PinMark { get; init; } = string.Empty;
    public bool IsPortalItem { get; init; }
    public ImageSource? Icon { get; init; }
    public double TileWidth { get; init; } = 92;
    public double NameFontSize { get; init; } = 11;

    public static VitrineItemView From(VitrineItem it, Guid vitrineId, bool isPortalItem, AppearanceSettings? appearance = null) => new()
    {
        Path = it.Path,
        DisplayName = string.IsNullOrWhiteSpace(it.DisplayName) ? System.IO.Path.GetFileName(it.Path) : it.DisplayName,
        KindGlyph = it.Kind switch { ItemKind.Folder => "▣", ItemKind.Shortcut => "↗", ItemKind.Application => "⬡", _ => "▢" },
        PinMark = it.IsPinned ? "• закреплено" : string.Empty,
        IsPortalItem = isPortalItem,
        Icon = IconHelper.GetIcon(it.Path),
        TileWidth = CalcTile(appearance),
        NameFontSize = 11 * (appearance?.FontScale ?? 1.0)
    };

    private static double CalcTile(AppearanceSettings? a)
    {
        var t = (double)(a?.TileSize ?? 92);
        if (string.Equals(a?.Density, "compact", StringComparison.OrdinalIgnoreCase)) t *= 0.82;
        if (string.Equals(a?.Density, "comfortable", StringComparison.OrdinalIgnoreCase)) t *= 1.15;
        return Math.Max(72, Math.Min(160, t));
    }
}