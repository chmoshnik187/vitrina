using Vitrines.Core.Storage;

namespace Vitrines.App.Desktop;

public interface IDesktopActions
{
    WorkspaceData Data { get; }
    Task SaveLayoutAsync();
    Task<string?> MoveFileToFolderAsync(string sourcePath, string targetFolder);
    void LogManual(string message);
}