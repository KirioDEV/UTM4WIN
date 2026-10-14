using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using UTM4WIN.App.ViewModels;
using UTM4WIN.Domain;

namespace UTM4WIN.App.Views;

public sealed partial class CreationWizard : UserControl {
    private readonly WindowId _windowId;

    public CreationWizardViewModel ViewModel {
        get;
    }

    public CreationWizard(CreationWizardViewModel viewModel, WindowId windowId) {
        ViewModel = viewModel;
        _windowId = windowId;
        InitializeComponent();
    }

    public Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void OnVirtualize(object sender, RoutedEventArgs args) => ViewModel.SelectExecutionMode(ExecutionMode.Whpx);
    private void OnEmulate(object sender, RoutedEventArgs args) => ViewModel.SelectExecutionMode(ExecutionMode.Tcg);
    private void OnLinux(object sender, RoutedEventArgs args) => ViewModel.SelectLinux();

    private async void OnBrowseIso(object sender, RoutedEventArgs args) => await PickAsync(".iso", isIso: true);
    private async void OnBrowseTool(object sender, RoutedEventArgs args) => await PickAsync(".exe", isIso: false);

    private async Task PickAsync(string extension, bool isIso) {
        try {
            var picker = new FileOpenPicker(_windowId);
            picker.FileTypeFilter.Add(extension);
            var result = await picker.PickSingleFileAsync();
            if (result is null)
                return;
            if (isIso)
                ViewModel.IsoPath = result.Path;
            else
                ViewModel.QemuImgPath = result.Path;
        } catch (Exception exception) {
            ViewModel.Error = $"File selection failed: {exception.Message}";
        }
    }
}
