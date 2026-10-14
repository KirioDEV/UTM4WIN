using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace UTM4WIN.App.ViewModels;

public enum ShellSection {
    Library,
    Settings
}

public enum AppTheme {
    System,
    Light,
    Dark
}

public sealed class ShellViewModel(ILogger<ShellViewModel> logger) : ObservableObject {
    private ShellSection _section = ShellSection.Library;
    private AppTheme _theme = AppTheme.System;

    public ShellSection Section {
        get => _section;
        set {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (!SetProperty(ref _section, value))
                return;
            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(IsLibrarySelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            logger.LogInformation("Navigated to {Section}.", value);
        }
    }

    public AppTheme Theme {
        get => _theme;
        set {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetProperty(ref _theme, value))
                logger.LogInformation("Selected theme {Theme}.", value);
        }
    }

    public string SectionTitle => Section == ShellSection.Library ? "Virtual machines" : "Settings";
    public bool IsLibrarySelected => Section == ShellSection.Library;
    public bool IsSettingsSelected => Section == ShellSection.Settings;
}