using System.Windows;
using Microsoft.Win32;

namespace DaDPlaylistManager.Services;

public static class Dialogs
{
    private const string Caption = "Dead as Disco Playlist Manager";

    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static void Info(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(string message) =>
        Show(message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Yes / No / Cancel. Returns null for cancel.</summary>
    public static bool? YesNoCancel(string message) =>
        Show(message, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, Caption, buttons, image)
            : MessageBox.Show(message, Caption, buttons, image);

    public static string[] OpenFiles(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileNames : [];
    }

    public static string? OpenFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static string? SaveFile(string title, string filter, string defaultName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static string? OpenFolder(string title, string? initial)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = initial ?? "" };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error("Couldn't open Explorer: " + ex.Message);
        }
    }
}
