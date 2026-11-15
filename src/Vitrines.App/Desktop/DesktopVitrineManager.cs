using System.Windows;
using Vitrines.Core.Models;
using Vitrines.Core.Storage;

namespace Vitrines.App.Desktop;

/// <summary>Открывает и синхронизирует окна-витрины с раскладкой рабочего пространства.</summary>
public sealed class DesktopVitrineManager
{
    private readonly IDesktopActions _actions;
    private readonly Dictionary<Guid, VitrineWindow> _windows = new();

    public DesktopVitrineManager(IDesktopActions actions) => _actions = actions;

    public void ShowAll()
    {
        var data = _actions.Data;
        var ids = data.Vitrines.Select(v => v.Id).ToHashSet();

        foreach (var id in _windows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            try { _windows[id].ForceClose(); } catch { }
            _windows.Remove(id);
        }

        foreach (var v in data.Vitrines)
        {
            EnsureOnScreen(v);
            if (_windows.TryGetValue(v.Id, out var w))
            {
                w.ApplyModel();
                if (!w.IsVisible) w.Show();
            }
            else
            {
                var win = new VitrineWindow(v, _actions);
                _windows[v.Id] = win;
                win.Show();
            }
        }
    }

    public void RefreshOpen()
    {
        var data = _actions.Data;
        var ids = data.Vitrines.Select(v => v.Id).ToHashSet();
        foreach (var id in _windows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            try { _windows[id].ForceClose(); } catch { }
            _windows.Remove(id);
        }
        foreach (var kv in _windows.ToList())
        {
            var model = data.Vitrines.FirstOrDefault(x => x.Id == kv.Key);
            if (model == null) continue;
            EnsureOnScreen(model);
            kv.Value.ApplyModel();
            kv.Value.RefreshItems();
        }
    }

    /// <summary>Вернуть все окна на слой рабочего стола (после смены обоев/разрешения/рестарта explorer).</summary>
    public void ReassertDesktopLayer() => RefreshOpen();

    /// <summary>Короткая вспышка витрины, в которую попал элемент (визуальный отклик на сортировку).</summary>
    public void PulseForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        foreach (var v in _actions.Data.Vitrines)
        {
            bool contains;
            if (v.Kind == VitrineKind.Portal)
                contains = !string.IsNullOrWhiteSpace(v.FolderPath) && VitrineStorage.IsInDirectory(path, v.FolderPath);
            else
                contains = v.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase) && i.Path.Length > 0);

            if (contains && _windows.TryGetValue(v.Id, out var window))
            {
                window.Pulse();
                return;
            }
        }
    }

    public void HideAll()
    {
        foreach (var w in _windows.Values.ToList()) { try { w.ForceClose(); } catch { } }
        _windows.Clear();
    }

    private static void EnsureOnScreen(Vitrine v)
    {
        // Восстанавливаем витрину в видимую область (мульти-монитор / смена разрешения / отключение монитора).
        // Точная поправка по физическим координатам монитора выполняется окном после показа.
        try
        {
            var vsLeft = SystemParameters.VirtualScreenLeft;
            var vsTop = SystemParameters.VirtualScreenTop;
            var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
            var work = SystemParameters.WorkArea;

            if (double.IsNaN(v.X) || double.IsInfinity(v.X)) v.X = work.Left + 60;
            if (double.IsNaN(v.Y) || double.IsInfinity(v.Y)) v.Y = work.Top + 60;
            if (double.IsNaN(v.Width) || double.IsInfinity(v.Width) || v.Width < 180) v.Width = 320;
            if (double.IsNaN(v.Height) || double.IsInfinity(v.Height) || v.Height < 120) v.Height = 240;

            v.Width = Math.Min(v.Width, Math.Max(220, vsRight - vsLeft - 40));
            v.Height = Math.Min(v.Height, Math.Max(160, vsBottom - vsTop - 40));

            if (v.X + 80 > vsRight) v.X = Math.Max(vsLeft + 20, vsRight - v.Width - 20);
            if (v.Y + 60 > vsBottom) v.Y = Math.Max(vsTop + 20, vsBottom - v.Height - 20);
            if (v.X < vsLeft - 40) v.X = vsLeft + 20;
            if (v.Y < vsTop - 40) v.Y = vsTop + 20;
        }
        catch { }
    }
}
