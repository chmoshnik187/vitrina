using Vitrines.Core.Models;

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
            _windows[id].Close();
            _windows.Remove(id);
        }

        foreach (var v in data.Vitrines)
        {
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
            _windows[id].Close();
            _windows.Remove(id);
        }
        foreach (var w in _windows.Values)
        {
            w.ApplyModel();
            w.RefreshItems();
        }
    }

    public void RefreshAll() => RefreshOpen();

    public void HideAll()
    {
        foreach (var w in _windows.Values.ToList()) w.Close();
        _windows.Clear();
    }
}
