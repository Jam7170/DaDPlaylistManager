using System.Windows;

namespace DaDPlaylistManager.Views;

/// <summary>A one-line text prompt.</summary>
public partial class InputWindow : Window
{
    private InputWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Returns the entered text, or null if cancelled.</summary>
    public static string? Ask(string title, string prompt, string initial)
    {
        var w = new InputWindow(title, prompt, initial) { Owner = Application.Current.MainWindow };
        return w.ShowDialog() == true ? w.InputBox.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
