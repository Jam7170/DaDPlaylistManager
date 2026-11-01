using System.Windows;
using DaDPlaylistManager.ViewModels;

namespace DaDPlaylistManager.Views;

public partial class ImportWindow : Window
{
    public ImportWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ImportViewModel vm) vm.RequestClose += (_, _) => Close();
        };
        Loaded += async (_, _) =>
        {
            if (DataContext is ImportViewModel vm) await vm.AnalyzeAllAsync();
        };
    }
}
