using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Vitrines.Core.Models;
using Vitrines.Core.Rules;
using Vitrines.Core.Storage;

namespace Vitrines.App.Desktop;

public partial class VitrineWindow : Window
{
    internal const string InternalDragFormat = "Vitrines.InternalItem";

    private readonly Vitrine _model;
    private readonly IDesktopActions _actions;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _layerTimer;
    private readonly DispatcherTimer _portalTimer;
    private bool _suppressSave;
    private bool _dragging;
    private bool _refreshing;
    private Point _dragStart;

    public ObservableCollection<VitrineItemView> Items { get; } = new();

    public VitrineWindow(Vitrine model, IDesktopActions actions)
    {
        InitializeComponent();
        _model = model;
        _actions = actions;
        ItemsPanel.ItemsSource = Items;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += async (_, _) => { _saveDebounce.Stop(); await _actions.SaveLayoutAsync(); };

        // Периодически возвращаем окна на слой рабочего стола (после смены фокуса/перезапуска shell).
        _layerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _layerTimer.Tick += (_, _) =>
        {
            if (!IsVisible || IsActive || _dragging) return;
            PinToDesktopLayer();
        };

        // Портал — живое отражение реальной папки: обновляем его без перезапуска приложения.
        _portalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _portalTimer.Tick += (_, _) =>
        {
            if (!IsVisible || _dragging || _refreshing || _model.Kind != VitrineKind.Portal) return;
            RefreshItems();
        };

        Loaded += OnLoaded;
        Unloaded += (_, _) => { _layerTimer.Stop(); _portalTimer.Stop(); };
        Deactivated += (_, _) => SendToBack();
        SourceInitialized += (_, _) => ApplyDesktopLayer(clamp: false);

        ApplyModel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDesktopLayer(clamp: true);
        ApplyAppearance(true);
        if (_model.Kind == VitrineKind.Portal) _portalTimer.Start();
        _layerTimer.Start();
    }

    public void ForceClose() { try { Close(); } catch { } }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
        ApplyDesktopLayer(clamp: false);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DPICHANGED = 0x02E0;

        if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED || msg == WM_SETTINGCHANGE)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ApplyDesktopLayer(clamp: true);
                RefreshItems();
            }, DispatcherPriority.Background);
        }
        else if (DesktopLayerInterop.TaskbarCreatedMessage != 0 && (uint)msg == DesktopLayerInterop.TaskbarCreatedMessage)
        {
            // Explorer перезапустился: перепривязываемся к слою рабочего стола.
            Dispatcher.InvokeAsync(() => ApplyDesktopLayer(clamp: true), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private AppearanceSettings CurrentAppearance =>
        _actions.Data.Watch.Appearance ?? new AppearanceSettings();

    private void ApplyDesktopLayer(bool clamp)
    {
        if (!IsLoaded && PresentationSource.FromVisual(this) == null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var appearance = CurrentAppearance;
        DesktopLayerInterop.ConfigureWindow(hwnd, appearance.NoActivateWindows);
        PinToDesktopLayer();
        if (clamp) DesktopLayerInterop.EnsureVisibleOnMonitor(hwnd);
    }

    private void PinToDesktopLayer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        DesktopLayerInterop.PinToBottom(hwnd, CurrentAppearance.PinToDesktopLayer);
        ApplyBackdrop(hwnd);
    }

    private void SendToBack()
    {
        if (!IsLoaded) return;
        PinToDesktopLayer();
    }

    private void ApplyBackdrop(IntPtr hwnd)
    {
        var a = CurrentAppearance;
        if (!a.UseAcrylic) { DesktopLayerInterop.DisableBackdrop(hwnd); return; }

        var palette = ResolvePalette(a);
        var alpha = (byte)Math.Clamp((int)(190 * ClampOpacity(_model.Style?.Opacity ?? 0.9)), 40, 230);
        var tint = palette.PanelBottom;
        var abgr = ((uint)alpha << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R;
        DesktopLayerInterop.ApplyBackdrop(hwnd, true, abgr);
    }

    public Guid VitrineId => _model.Id;

    public void ApplyModel()
    {
        _suppressSave = true;
        TitleText.Text = _model.Name;
        Left = _model.X; Top = _model.Y; Width = _model.Width; Height = _model.Height;
        ApplyAppearance(false);
        RefreshItems();
        _suppressSave = false;
        SendToBack();
    }

    private static double ClampOpacity(double op) => Math.Clamp(op <= 0 ? 0.9 : op, 0.35, 1.0);

    private void ApplyAppearance(bool animate)
    {
        var a = CurrentAppearance;
        var palette = ResolvePalette(a);
        var accent = ParseColor(a.Accent, Color.FromRgb(0x7C, 0x9C, 0xFF));

        SetBrush("VAccent", accent);
        Resources["VAccentColor"] = accent;
        SetBrush("VText", palette.Text);
        SetBrush("VTextDim", palette.TextDim);
        SetBrush("VHeaderBg", palette.Header);
        SetBrush("VItemBg", palette.ItemBg);
        SetBrush("VItemBorder", palette.ItemBorder);
        SetBrush("VItemHover", palette.ItemHover);
        SetBrush("VBadgeBg", WithAlpha(accent, 0x2E));
        SetBrush("VBadgeBorder", WithAlpha(accent, 0x77));
        SetBrush("VEmpty", palette.Empty);

        var opacity = ClampOpacity(_model.Style?.Opacity ?? 0.9);
        RootBorder.Opacity = opacity;
        RootBorder.Background = new LinearGradientBrush(palette.PanelTop, palette.PanelBottom, 90);
        RootBorder.BorderBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(palette.BorderTop, 0),
                new(palette.BorderMid, 0.55),
                new(accent, 1)
            }
        };

        var tile = a.TileSize <= 0 ? 92 : a.TileSize;
        if (string.Equals(a.Density, "compact", StringComparison.OrdinalIgnoreCase)) tile = (int)(tile * 0.82);
        if (string.Equals(a.Density, "comfortable", StringComparison.OrdinalIgnoreCase)) tile = (int)(tile * 1.15);
        Resources["TileWidth"] = (double)Math.Max(72, Math.Min(160, tile));
        Resources["FontScale"] = a.FontScale <= 0 ? 1.0 : a.FontScale;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) ApplyBackdrop(hwnd);

        if (animate && a.Animations && IsLoaded)
        {
            RootScale.ScaleX = RootScale.ScaleY = 0.965;
            RootBorder.Opacity = 0;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
            RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    private static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color c) return c;
        }
        catch { }
        return fallback;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private ThemePalette ResolvePalette(AppearanceSettings a)
    {
        var accent = ParseColor(a.Accent, Color.FromRgb(0x7C, 0x9C, 0xFF));
        return (a.Theme ?? "dark").Trim().ToLowerInvariant() switch
        {
            "light" => new ThemePalette(
                PanelTop: Color.FromArgb(0xEE, 0xF7, 0xF9, 0xFD), PanelBottom: Color.FromArgb(0xF2, 0xE9, 0xEE, 0xF7),
                Text: Color.FromRgb(0x1B, 0x22, 0x32), TextDim: Color.FromRgb(0x5B, 0x68, 0x80),
                Header: Color.FromArgb(0x26, 0x00, 0x00, 0x00), ItemBg: Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                ItemBorder: Color.FromArgb(0x22, 0x00, 0x00, 0x00), ItemHover: Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
                Empty: Color.FromArgb(0x99, 0x5B, 0x68, 0x80),
                BorderTop: Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), BorderMid: Color.FromArgb(0x44, 0x00, 0x00, 0x00)),
            "glass" => new ThemePalette(
                PanelTop: Color.FromArgb(0x5A, 0x18, 0x22, 0x34), PanelBottom: Color.FromArgb(0x46, 0x10, 0x16, 0x1F),
                Text: Color.FromRgb(0xEA, 0xF2, 0xFF), TextDim: Color.FromRgb(0x9F, 0xB2, 0xD0),
                Header: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), ItemBg: Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
                ItemBorder: Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), ItemHover: Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
                Empty: Color.FromArgb(0x80, 0xA8, 0xB8, 0xD8),
                BorderTop: Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF), BorderMid: Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            _ => new ThemePalette(
                PanelTop: Color.FromArgb(0xCC, 0x14, 0x19, 0x22), PanelBottom: Color.FromArgb(0xD6, 0x10, 0x14, 0x1C),
                Text: Color.FromRgb(0xF4, 0xF7, 0xFF), TextDim: Color.FromRgb(0xA8, 0xB8, 0xD8),
                Header: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), ItemBg: Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
                ItemBorder: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), ItemHover: Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
                Empty: Color.FromArgb(0x70, 0xA8, 0xB8, 0xD8),
                BorderTop: Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), BorderMid: Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
        };
    }

    private sealed record ThemePalette(
        Color PanelTop, Color PanelBottom, Color Text, Color TextDim, Color Header,
        Color ItemBg, Color ItemBorder, Color ItemHover, Color Empty, Color BorderTop, Color BorderMid);

    public void RefreshItems()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            Items.Clear();
            var appearance = CurrentAppearance;
            if (_model.Kind == VitrineKind.Portal)
                foreach (var it in EnumeratePortalItems()) Items.Add(VitrineItemView.From(it, _model.Id, true, appearance));
            else
                foreach (var it in _model.Items) Items.Add(VitrineItemView.From(it, _model.Id, false, appearance));
            EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            KindBadge.Text = _model.Kind == VitrineKind.Portal ? $"портал · {Items.Count}" : $"витрина · {Items.Count}";
        }
        finally { _refreshing = false; }
    }

    private IEnumerable<VitrineItem> EnumeratePortalItems()
    {
        if (string.IsNullOrWhiteSpace(_model.FolderPath) || !Directory.Exists(_model.FolderPath))
            return Enumerable.Empty<VitrineItem>();
        try
        {
            return Directory.EnumerateFileSystemEntries(_model.FolderPath)
                .OrderBy(p => Directory.Exists(p) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new VitrineItem
                {
                    Path = p,
                    DisplayName = Path.GetFileName(p),
                    Kind = FileFacts.DetectKind(p)
                })
                .ToList();
        }
        catch { return Enumerable.Empty<VitrineItem>(); }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_model.Kind == VitrineKind.Portal && !string.IsNullOrWhiteSpace(_model.FolderPath) && Directory.Exists(_model.FolderPath))
                TryOpen(_model.FolderPath);
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
            // после перетаскивания снова удерживаем окно на слое рабочего стола
            SendToBack();
        }
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
        _dragging = true;
        try { DragDrop.DoDragDrop(fe, data, DragDropEffects.Move); }
        finally { _dragging = false; SendToBack(); }
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
    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e) => ScheduleSaveBounds();
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
        _dragging = true;
        try
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
                    var useReference = _actions.Data.Watch.CollectionPlacement == CollectionPlacement.Reference;
                    if (!useReference && (File.Exists(p) || Directory.Exists(p)))
                    {
                        var storage = VitrineStorage.GetManagedFolder(_model);
                        if (!VitrineStorage.IsInDirectory(p, storage))
                        {
                            var moved = await _actions.MoveFileToFolderAsync(p, storage);
                            if (moved != null) pathToAdd = moved;
                        }
                    }
                    if (_model.Items.Any(i => string.Equals(i.Path, pathToAdd, StringComparison.OrdinalIgnoreCase))) continue;
                    _model.Items.Add(new VitrineItem { Path = pathToAdd, DisplayName = Path.GetFileName(pathToAdd), Kind = FileFacts.DetectKind(pathToAdd), IsPinned = true });
                }
            }
            RefreshItems();
            await _actions.SaveLayoutAsync();
            e.Handled = true;
        }
        finally { _dragging = false; SendToBack(); }
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
        var useReference = _actions.Data.Watch.CollectionPlacement == CollectionPlacement.Reference;
        var storage = VitrineStorage.GetManagedFolder(_model);
        if (!useReference && (File.Exists(item.Path) || Directory.Exists(item.Path)) && !VitrineStorage.IsInDirectory(item.Path, storage))
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
        _portalTimer.Start();
        await _actions.SaveLayoutAsync();
        _actions.LogManual($"Витрина настроена как портал: {_model.Name}");
    }

    private async void MakeCollection_Click(object sender, RoutedEventArgs e)
    {
        _model.Kind = VitrineKind.Collection;
        _model.FolderPath = null;
        _portalTimer.Stop();
        ApplyModel();
        await _actions.SaveLayoutAsync();
        _actions.LogManual($"Витрина переведена в режим коллекции: {_model.Name}");
    }

    private void RefreshContents_Click(object sender, RoutedEventArgs e) => RefreshItems();

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

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile) return;
        if (!CurrentAppearance.Animations) return;
        try
        {
            var index = tile.DataContext is VitrineItemView view ? Items.IndexOf(view) : 0;
            var begin = TimeSpan.FromMilliseconds(Math.Min(240, Math.Max(0, index) * 14));
            tile.RenderTransform = new TranslateTransform(0, 9);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            tile.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { BeginTime = begin, EasingFunction = ease });
            ((TranslateTransform)tile.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(210)) { BeginTime = begin, EasingFunction = ease });
        }
        catch { }
    }

    /// <summary>Короткая акцентная вспышка — визуальный отклик на авто-размещение/перемещение.</summary>
    public void Pulse()
    {
        if (!CurrentAppearance.Animations || !IsLoaded) return;
        try
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620))));
            PulseBorder.BeginAnimation(OpacityProperty, animation);
        }
        catch { }
    }

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
