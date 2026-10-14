using Microsoft.UI.Xaml.Controls;
using UTM4WIN.App.ViewModels;

namespace UTM4WIN.App.Views;

public sealed partial class MetadataEditor : UserControl {
    public MetadataEditorViewModel ViewModel {
        get;
    }

    public MetadataEditor(MetadataEditorViewModel viewModel) {
        ViewModel = viewModel;
        InitializeComponent();
    }
}