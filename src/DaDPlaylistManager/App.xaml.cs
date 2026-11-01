using System.Windows;
using System.Windows.Threading;
using DaDPlaylistManager.Services;

namespace DaDPlaylistManager;

public partial class App : Application
{
    public App()
    {
        // Let numeric TextBoxes bound with UpdateSourceTrigger=PropertyChanged keep what the user
        // typed (e.g. "12.") instead of being rewritten to the parsed value mid-edit.
        FrameworkCompatibilityPreferences.KeepTextBoxDisplaySynchronizedWithTextProperty = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialogs.Error("Something went wrong:\n\n" + e.Exception.Message);
        e.Handled = true;
    }
}
