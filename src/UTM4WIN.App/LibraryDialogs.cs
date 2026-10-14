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
            Title = "Create virtual machine",
            Content = new CreationWizard(wizard, WindowId),
            PrimaryButtonText = "Next",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        bool discarded = false;
        bool discardOffered = false;
        void UpdateButtons() {
            dialog.PrimaryButtonText = wizard.Step == 2 ? "Create VM" : "Next";
            dialog.SecondaryButtonText = discardOffered ? "Discard changes" : wizard.Step > 0 ? "Back" : "";
        }
        dialog.PrimaryButtonClick += async (_, args) => {
            var deferral = args.GetDeferral();
            try {
                discardOffered = false;
                if (wizard.Step < 2) {
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
        await dialog.ShowAsync();
    }

    public async Task EditAsync(MetadataEditorViewModel editor, bool isNew, Func<VmConfiguration, Task<bool>> save) {
        var dialog = new ContentDialog {
            XamlRoot = XamlRoot ?? throw new InvalidOperationException("The window is not loaded."),
            RequestedTheme = Theme,
            Title = isNew ? "Add VM metadata" : "Edit VM metadata",
            Content = new MetadataEditor(editor),
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        bool saved = false;
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
        await dialog.ShowAsync();
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
