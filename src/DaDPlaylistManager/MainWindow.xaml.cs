using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DaDPlaylistManager.Core.Audio;
using DaDPlaylistManager.Core.Packs;
using DaDPlaylistManager.ViewModels;

namespace DaDPlaylistManager;

public partial class MainWindow : Window
{
    private const string EntriesFormat = "DaD.PlaylistEntries";
    private const string SongsFormat = "DaD.Songs";

    private readonly MainViewModel _vm;
    private Point _dragStart;
    private bool _restoringSelection;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.EntriesReordered += (_, items) => RestoreEntrySelection(items);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitFocusedTextBox();
        _vm.OnClosing(e);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Keyboard shortcuts don't move focus, so push any pending TextBox edit into the view model first.
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) CommitFocusedTextBox();
        base.OnPreviewKeyDown(e);
    }

    private static void CommitFocusedTextBox()
    {
        if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = _vm;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // ---- Player ----

    private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e) => _vm.IsUserSeeking = true;

    private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _vm.IsUserSeeking = false;
        _vm.PlaybackPosition = PositionSlider.Value;
    }

    private void SongsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder<DataGridRow>(e.OriginalSource) is { Item: SongViewModel song }) _vm.PlaySongCommand.Execute(song);
    }

    private void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder<ListBoxItem>(e.OriginalSource) is { DataContext: PlaylistEntryViewModel { Song: { } song } }) _vm.PlaySongCommand.Execute(song);
    }

    private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder<ListBoxItem>(e.OriginalSource) is { DataContext: SongViewModel song } && _vm.SelectedPlaylist is { } pl)
            _vm.AddSongsToPlaylist(pl, [song], pl.Entries.Count);
    }

    // ---- Playlist entry selection ----

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;
        _vm.SelectedEntries = EntriesList.SelectedItems.Cast<PlaylistEntryViewModel>().ToList();
    }

    private void RestoreEntrySelection(List<PlaylistEntryViewModel> items)
    {
        _restoringSelection = true;
        EntriesList.SelectedItems.Clear();
        foreach (var i in items) EntriesList.SelectedItems.Add(i);
        _restoringSelection = false;
        _vm.SelectedEntries = items;
        if (items.Count > 0) EntriesList.ScrollIntoView(items[0]);
    }

    private void EntriesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            _vm.RemoveFromPlaylistCommand.Execute(EntriesList.SelectedItems);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            _vm.MoveEntriesCommand.Execute(e.SystemKey == Key.Up ? "up" : "down");
            e.Handled = true;
        }
    }

    // ---- Drag and drop between lists ----

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private bool DragThresholdPassed(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return false;
        var d = _dragStart - e.GetPosition(null);
        return Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void EntriesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!DragThresholdPassed(e) || ItemUnder<ListBoxItem>(e.OriginalSource) is null) return;
        var items = EntriesList.SelectedItems.Cast<PlaylistEntryViewModel>().OrderBy(i => _vm.SelectedPlaylist!.Entries.IndexOf(i)).ToList();
        if (items.Count == 0) return;
        DragDrop.DoDragDrop(EntriesList, new DataObject(EntriesFormat, items), DragDropEffects.Move);
        HideDropIndicator();
    }

    private void AvailableList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!DragThresholdPassed(e) || ItemUnder<ListBoxItem>(e.OriginalSource) is null) return;
        var items = AvailableList.SelectedItems.Cast<SongViewModel>().ToList();
        if (items.Count == 0) return;
        DragDrop.DoDragDrop(AvailableList, new DataObject(SongsFormat, items), DragDropEffects.Copy);
        HideDropIndicator();
    }

    private void EntriesList_DragOver(object sender, DragEventArgs e)
    {
        if (_vm.SelectedPlaylist is null || !(e.Data.GetDataPresent(EntriesFormat) || e.Data.GetDataPresent(SongsFormat)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = e.Data.GetDataPresent(EntriesFormat) ? DragDropEffects.Move : DragDropEffects.Copy;
        ShowDropIndicator(DropIndex(e, out double y), y);
        AutoScroll(e);
        e.Handled = true;
    }

    private void EntriesList_DragLeave(object sender, DragEventArgs e) => HideDropIndicator();

    private void EntriesList_Drop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        if (_vm.SelectedPlaylist is not { } pl) return;
        int index = DropIndex(e, out _);
        if (e.Data.GetData(EntriesFormat) is List<PlaylistEntryViewModel> entries)
        {
            _vm.SelectedEntries = entries;
            _vm.MoveEntriesTo(pl, entries, index);
        }
        else if (e.Data.GetData(SongsFormat) is List<SongViewModel> songs)
        {
            _vm.AddSongsToPlaylist(pl, songs, index);
        }
        e.Handled = true;
    }

    /// <summary>Insertion index for a drop at the mouse position, and the y (relative to the list) to draw the marker.</summary>
    private int DropIndex(DragEventArgs e, out double y)
    {
        var pl = _vm.SelectedPlaylist!;
        for (int i = 0; i < pl.Entries.Count; i++)
        {
            if (EntriesList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item || !item.IsVisible) continue;
            var pos = e.GetPosition(item);
            if (pos.Y < item.ActualHeight / 2)
            {
                y = item.TranslatePoint(new Point(0, 0), EntriesList).Y;
                return i;
            }
        }
        // Below the last visible item.
        y = 0;
        for (int i = pl.Entries.Count - 1; i >= 0; i--)
        {
            if (EntriesList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsVisible: true } last)
            {
                var lastPos = e.GetPosition(last);
                if (lastPos.Y < 0) continue;
                y = last.TranslatePoint(new Point(0, last.ActualHeight), EntriesList).Y;
                return i + 1;
            }
        }
        return pl.Entries.Count;
    }

    private void ShowDropIndicator(int index, double y)
    {
        DropIndicator.Width = Math.Max(0, EntriesList.ActualWidth - 24);
        Canvas.SetLeft(DropIndicator, 8);
        Canvas.SetTop(DropIndicator, Math.Max(0, y - 1));
        DropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator() => DropIndicator.Visibility = Visibility.Collapsed;

    private void AutoScroll(DragEventArgs e)
    {
        if (FindChild<ScrollViewer>(EntriesList) is not { } sv) return;
        double y = e.GetPosition(EntriesList).Y;
        if (y < 30) sv.LineUp();
        else if (y > EntriesList.ActualHeight - 30) sv.LineDown();
    }

    // ---- Files dropped onto the window ----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        var packs = files.Where(f => f.EndsWith(SongPack.Extension, StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        var audio = files.Where(f => AudioImporter.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();
        foreach (var p in packs) await _vm.ImportPackFile(p);
        if (audio.Length > 0) _vm.ImportAudioFiles(audio);
    }

    // ---- Helpers ----

    private static T? ItemUnder<T>(object source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
