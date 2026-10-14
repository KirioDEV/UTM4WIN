using System.ComponentModel;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTM4WIN.App.ViewModels;

namespace UTM4WIN.App;

public sealed partial class MainWindow : Window {
    public ShellViewModel ViewModel {
        get;
    }
    public LibraryViewModel Library {
        get;
    }

    public MainWindow(ShellViewModel viewModel, LibraryViewModel library, LibraryDialogs dialogs) {
        ViewModel = viewModel;
        Library = library;
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1100, 760));
        Root.Loaded += async (_, _) => {
            ApplyTitleBarTheme();
            dialogs.XamlRoot = Root.XamlRoot;
            dialogs.WindowId = AppWindow.Id;
            dialogs.Theme = Root.ActualTheme;
            await Library.ReloadCommand.ExecuteAsync(null);
        };
        Root.ActualThemeChanged += (_, _) => { ApplyTitleBarTheme(); dialogs.Theme = Root.ActualTheme; };
        ViewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
        AppWindow.Closing += (_, args) => args.Cancel = Library.IsBusy;
    }

    public Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyTitleBarTheme() => AppWindow.TitleBar.PreferredTheme =
        Root.ActualTheme == ElementTheme.Dark
            ? TitleBarTheme.Dark
            : TitleBarTheme.Light;

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args) {
        bool wide = args.NewSize.Width >= 820;
        LibrarySplit.DisplayMode = wide ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        LibrarySplit.IsPaneOpen = wide;
    }

    private void OnSidebarToggle(object sender, RoutedEventArgs args) => LibrarySplit.IsPaneOpen = !LibrarySplit.IsPaneOpen;
    private void OnLibraryClicked(object sender, RoutedEventArgs args) {
        ViewModel.Section = ShellSection.Library;
        if (LibrarySplit.DisplayMode == SplitViewDisplayMode.Overlay)
            LibrarySplit.IsPaneOpen = false;
    }
    private void OnSettingsClicked(object sender, RoutedEventArgs args) {
        ViewModel.Section = ShellSection.Settings;
        if (LibrarySplit.DisplayMode == SplitViewDisplayMode.Overlay)
            LibrarySplit.IsPaneOpen = false;
    }
    private void OnMachineSelectionChanged(object sender, SelectionChangedEventArgs args) {
        ViewModel.Section = ShellSection.Library;
        if (LibrarySplit.DisplayMode == SplitViewDisplayMode.Overlay)
            LibrarySplit.IsPaneOpen = false;
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs args) => Preview.Height = Math.Clamp(args.NewSize.Width * 9 / 16, 190, 420);

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args) {
        if (sender is ComboBox { SelectedIndex: >= 0 } picker)
            ViewModel.Theme = (AppTheme)picker.SelectedIndex;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args) {
        if (args.PropertyName != nameof(ShellViewModel.Theme))
            return;
        Root.RequestedTheme = ViewModel.Theme switch {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}

