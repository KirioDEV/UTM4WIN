using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTM4WIN.App.ViewModels;
using UTM4WIN.App.Views;
using UTM4WIN.Domain;

namespace UTM4WIN.App;

public sealed class LibraryDialogs : ILibraryDialogs {
    public WindowId WindowId {
        private get; set;
    }
    public ElementTheme Theme {
        private get; set;
    }
    public XamlRoot? XamlRoot {
        private get; set;
    }

    public async Task CreateAsync(CreationWizardViewModel wizard) {
        var dialog = new ContentDialog {
            XamlRoot = XamlRoot ?? throw new InvalidOperationException("The window is not loaded."),
            RequestedTheme = Theme,
            Title = "Start",
            Content = new CreationWizard(wizard, WindowId),
            PrimaryButtonText = "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        bool discarded = false;
        bool discardOffered = false;
        void UpdateButtons() {
            dialog.Title = wizard.StepTitle;
            dialog.PrimaryButtonText = wizard.Step == 5 ? "Create VM" : wizard.Step < 2 ? "" : "Continue";
            dialog.SecondaryButtonText = discardOffered ? "Discard changes" : wizard.Step > 0 ? "Back" : "";
        }
        dialog.PrimaryButtonClick += async (_, args) => {
            var deferral = args.GetDeferral();
            try {
                discardOffered = false;
                if (wizard.Step < 5) {
                    wizard.Next();
                    args.Cancel = true;
                } else
                    args.Cancel = !await wizard.CreateAsync();
                UpdateButtons();
            } finally { deferral.Complete(); }
        };
        dialog.SecondaryButtonClick += (_, args) => {
            if (wizard.IsCreating) {
                args.Cancel = true;
                return;
            }
            if (discardOffered)
                discarded = true;
            else {
                wizard.Back();
                args.Cancel = true;
                UpdateButtons();
            }
        };
        dialog.Closing += (_, args) => {
            if (wizard.IsCreating) {
                wizard.CancelCreationCommand.Execute(null);
                args.Cancel = true;
                return;
            }
            if (wizard.CreatedMachine is null && !discarded && wizard.HasChanges) {
                args.Cancel = true;
                discardOffered = true;
                wizard.Error = "You have unsaved changes. Continue creating or choose Discard changes.";
                UpdateButtons();
            }
        };
        void OnWizardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) {
            if (args.PropertyName == nameof(CreationWizardViewModel.Step))
                UpdateButtons();
        }
        wizard.PropertyChanged += OnWizardChanged;
        try {
            await dialog.ShowAsync();
        } finally { wizard.PropertyChanged -= OnWizardChanged; }
    }

    public async Task EditAsync(MetadataEditorViewModel editor, bool isNew, Func<VmConfiguration, Task<bool>> save) {
        var root = XamlRoot ?? throw new InvalidOperationException("The window is not loaded.");
        var content = new MetadataEditor(editor);
        void ResizeContent(XamlRoot sender, XamlRootChangedEventArgs args) =>
            content.Width = Math.Clamp(sender.Size.Width - 128, 400, 680);
        content.Width = Math.Clamp(root.Size.Width - 128, 400, 680);
        var dialog = new ContentDialog {
            XamlRoot = root,
            RequestedTheme = Theme,
            Title = isNew ? "Add VM metadata" : "Virtual machine settings",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        bool saved = false;
        dialog.Resources["ContentDialogMaxWidth"] = 820d;
        bool discarded = false;
        bool saving = false;
        dialog.PrimaryButtonClick += async (_, args) => {
            var deferral = args.GetDeferral();
            saving = true;
            try {
                var configuration = editor.Validate();
                saved = configuration is not null && await save(configuration);
                args.Cancel = !saved;
            } finally { saving = false; deferral.Complete(); }
        };
        dialog.SecondaryButtonClick += (_, _) => discarded = true;
        dialog.Closing += (_, args) => {
            if (saving) {
                args.Cancel = true;
                return;
            }
            if (!saved && !discarded && editor.HasChanges) {
                args.Cancel = true;
                editor.Error = "You have unsaved changes. Save or choose Discard changes.";
                dialog.SecondaryButtonText = "Discard changes";
            }
        };
        root.Changed += ResizeContent;
        try {
            await dialog.ShowAsync();
        } finally { root.Changed -= ResizeContent; }
    }

    public async Task<bool> ConfirmRemoveAsync(string name) {
        var dialog = new ContentDialog {
            XamlRoot = XamlRoot ?? throw new InvalidOperationException("The window is not loaded."),
            RequestedTheme = Theme,
            Title = "Remove from library?",
            Content = $"Remove '{name}' from the library? Its configuration, disks and other files will be kept. You can register retained metadata again.",
            PrimaryButtonText = "Remove entry",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
