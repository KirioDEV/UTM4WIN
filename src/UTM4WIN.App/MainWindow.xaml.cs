using System.ComponentModel;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTM4WIN.App.ViewModels;
using UTM4WIN.App.Views;
using UTM4WIN.Application;

namespace UTM4WIN.App;

public sealed partial class MainWindow : Window {
    public ShellViewModel ViewModel {
        get;
    }
    public LibraryViewModel Library {
        get;
    }
    public SessionViewModel Session {
        get;
    }
    private bool _closePending;
    private bool _allowClose;
    private readonly Dictionary<Guid, ConsoleWindow> _consoles = [];

    public MainWindow(ShellViewModel viewModel, LibraryViewModel library, LibraryDialogs dialogs, SessionViewModel session) {
        ViewModel = viewModel;
        Library = library;
        Session = session;
        InitializeComponent();
        Session.Dispatch = callback => DispatcherQueue.TryEnqueue(() => callback());
        Session.ShowConsole = (snapshot, display) => {
            if (_consoles.TryGetValue(snapshot.Configuration.Id, out var previous) && !ReferenceEquals(previous.Display, display)) {
                _consoles.Remove(snapshot.Configuration.Id);
                previous.Close();
            }
            if (!_consoles.TryGetValue(snapshot.Configuration.Id, out var console)) {
                console = new(snapshot.Configuration.Name, display, Root.ActualTheme);
                _consoles.Add(snapshot.Configuration.Id, console);
                var created = console;
                console.Closed += (_, _) => {
                    if (_consoles.TryGetValue(snapshot.Configuration.Id, out var current) && ReferenceEquals(current, created))
                        _consoles.Remove(snapshot.Configuration.Id);
                };
            }
            console.Activate();
        };
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
        Closed += (_, _) => {
            ViewModel.PropertyChanged -= OnViewModelChanged;
        };
        AppWindow.Closing += async (_, args) => {
            if (_allowClose)
                return;
            args.Cancel = true;
            if (Library.IsBusy || _closePending)
                return;
            _closePending = true;
            try {
                if (!Session.HasActiveSessions || await Session.PrepareCloseAsync()) {
                    foreach (var console in _consoles.Values.ToArray())
                        await console.CloseConsoleAsync();
                    _allowClose = true;
                    Close();
                }
            } finally { _closePending = false; }
        };
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

