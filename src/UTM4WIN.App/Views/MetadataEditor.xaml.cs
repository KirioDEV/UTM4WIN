using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using UTM4WIN.App.ViewModels;

namespace UTM4WIN.App.Views;

public sealed partial class MetadataEditor : UserControl {
    public MetadataEditorViewModel ViewModel {
        get;
    }
    public Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public MetadataEditor(MetadataEditorViewModel viewModel) {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
