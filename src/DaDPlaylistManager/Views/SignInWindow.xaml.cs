using System.Windows;
using System.Windows.Input;
using DaDPlaylistManager.Core.Sync;

namespace DaDPlaylistManager.Views;

public sealed record SignInResult(string Server, string Token, SyncPlayer Player);

/// <summary>Signs in to the group website with an existing player name + password.</summary>
public partial class SignInWindow : Window
{
    public SignInWindow(string server)
    {
        InitializeComponent();
        ServerBox.Text = server;
        Loaded += async (_, _) => await LoadPlayers();
    }

    public SignInResult? Result { get; private set; }

    private string Server => ServerBox.Text.Trim().TrimEnd('/');

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadPlayers()
    {
        ShowError(null);
        if (!Uri.TryCreate(Server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ShowError("Enter the website address, e.g. https://heroicgames.uk");
            return;
        }
        try
        {
            IsEnabled = false;
            using var client = new SyncClient(Server);
            var players = await client.GetLoginPlayersAsync();
            PlayerBox.ItemsSource = players.Where(p => p.Claimed).ToList();
            PlayerBox.SelectedIndex = PlayerBox.Items.Count > 0 ? 0 : -1;
            if (PlayerBox.Items.Count == 0) ShowError("Nobody has set a password on this site yet. Log in on the website first.");
            else PasswordBox.Focus();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't reach {Server}: {ex.Message}");
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await LoadPlayers();

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SignIn_Click(sender, e);
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerBox.SelectedItem is not LoginPlayer player)
        {
            ShowError("Pick your name.");
            return;
        }
        ShowError(null);
        try
        {
            IsEnabled = false;
            using var client = new SyncClient(Server);
            var me = await client.LoginAsync(player.Id, PasswordBox.Password);
            Result = new SignInResult(Server, client.SessionToken!, me);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
