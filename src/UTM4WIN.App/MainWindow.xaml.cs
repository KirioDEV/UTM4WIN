using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTM4WIN.App.ViewModels;

namespace UTM4WIN.App;

public sealed partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }
    public LibraryViewModel Library { get; }

    public MainWindow(ShellViewModel viewModel, LibraryViewModel library, LibraryDialogs dialogs)
    {
        ViewModel = viewModel;
        Library = library;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        Navigation.SelectedItem = LibraryItem;
        Root.Loaded += async (_, _) =>
        {
            ApplyTitleBarTheme();
            dialogs.XamlRoot = Root.XamlRoot;
            await Library.ReloadCommand.ExecuteAsync(null);
        };
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        ViewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
        AppWindow.Closing += (_, args) => args.Cancel = Library.IsBusy;
    }

    public Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyTitleBarTheme() => AppWindow.TitleBar.PreferredTheme =
        Root.ActualTheme == ElementTheme.Dark
            ? Microsoft.UI.Windowing.TitleBarTheme.Dark
            : Microsoft.UI.Windowing.TitleBarTheme.Light;

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) =>
        ViewModel.Section = args.IsSettingsSelected ? ShellSection.Settings : ShellSection.Library;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedIndex: >= 0 } picker) ViewModel.Theme = (AppTheme)picker.SelectedIndex;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ShellViewModel.Theme)) return;
        Root.RequestedTheme = ViewModel.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}

