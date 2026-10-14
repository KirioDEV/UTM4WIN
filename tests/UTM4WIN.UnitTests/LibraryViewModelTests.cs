using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UTM4WIN.App;
using UTM4WIN.App.ViewModels;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class LibraryViewModelTests
{
    [TestMethod]
    public void EditorRejectsFractionalAndEmptyNumbersWithoutMutatingSnapshot()
    {
        var snapshot = new VmSnapshot(VmConfiguration.CreateMetadata("Original"), "revision");
        var editor = new MetadataEditorViewModel(snapshot) { Name = "Draft", CpuCount = 1.5 };
        Assert.IsNull(editor.Validate());
        Assert.IsTrue(editor.HasChanges);
        editor.CpuCount = 2;
        editor.MemoryMiB = double.NaN;
        Assert.IsNull(editor.Validate());
        Assert.AreEqual("Original", snapshot.Configuration.Name);
        Assert.AreEqual(2048, snapshot.Configuration.MemoryMiB);
    }

    [TestMethod]
    public async Task SavingConflictLeavesDraftAndMessageAvailableToDialog()
    {
        var snapshot = new VmSnapshot(VmConfiguration.CreateMetadata("Original"), "revision");
        var dialogs = new TestDialogs
        {
            Edit = async (editor, save) =>
            {
                editor.Name = "Unsaved edit";
                Assert.IsFalse(await save(editor.Validate()!));
                Assert.AreEqual("Unsaved edit", editor.Name);
                Assert.IsTrue(editor.HasChanges);
                Assert.AreEqual("Concurrent edit", editor.Error);
            }
        };
        var model = CreateModel(new TestRepository(snapshot), dialogs);
        await model.ReloadCommand.ExecuteAsync(null);
        model.SelectedMachine = model.Machines.Single();
        await model.EditCommand.ExecuteAsync(null);
        Assert.AreEqual("Original", model.Machines.Single().Name);
        Assert.IsFalse(model.IsBusy);
    }

    [TestMethod]
    public async Task BusyDialogPreventsOtherMutationsAndCanceledRemovalKeepsEntry()
    {
        var snapshot = new VmSnapshot(VmConfiguration.CreateMetadata("Original"), "revision");
        var entered = new TaskCompletionSource();
        var leave = new TaskCompletionSource();
        var dialogs = new TestDialogs { Edit = async (_, _) => { entered.SetResult(); await leave.Task; } };
        var repository = new TestRepository(snapshot);
        var model = CreateModel(repository, dialogs);
        await model.ReloadCommand.ExecuteAsync(null);
        model.SelectedMachine = model.Machines.Single();
        var edit = model.EditCommand.ExecuteAsync(null);
        await entered.Task;
        Assert.IsTrue(model.IsBusy);
        Assert.IsFalse(model.AddCommand.CanExecute(null));
        Assert.IsFalse(model.RemoveCommand.CanExecute(null));
        Assert.IsFalse(model.ReloadCommand.CanExecute(null));
        leave.SetResult();
        await edit;
        await model.RemoveCommand.ExecuteAsync(null);
        Assert.AreEqual(0, repository.RemovalCount);
        Assert.AreEqual(1, model.Machines.Count);
    }

    [TestMethod]
    public void LaunchArgumentsAreExplicitAndVmRowHasAccessibleName()
    {
        Assert.IsNull(LaunchOptions.ParseLibraryRoot([]));
        Assert.AreEqual(@"C:\Library", LaunchOptions.ParseLibraryRoot(["--library-root", @"C:\Library"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.ParseLibraryRoot(["-extra-qemu-args", "anything"]));
        Assert.AreEqual("Debian", new VmListItem(new(VmConfiguration.CreateMetadata("Debian"), "revision")).ToString());
    }

    private sealed class TestDialogs : ILibraryDialogs
    {
        public Task CreateAsync(CreationWizardViewModel wizard) => Task.CompletedTask;
        public Func<MetadataEditorViewModel, Func<VmConfiguration, Task<bool>>, Task> Edit { get; init; } = (_, _) => Task.CompletedTask;
        public Task EditAsync(MetadataEditorViewModel editor, bool isNew, Func<VmConfiguration, Task<bool>> save) => Edit(editor, save);
        public Task<bool> ConfirmRemoveAsync(string name) => Task.FromResult(false);
    }

    private static LibraryViewModel CreateModel(IVmRepository repository, ILibraryDialogs dialogs) => new(repository, dialogs,
        NullLogger<LibraryViewModel>.Instance, new TestCreation(), new QemuToolSelection(), NullLogger<CreationWizardViewModel>.Instance);

    private sealed class TestCreation : IVmCreationService
    {
        public Task<VmSnapshot> CreateAsync(VmCreationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestRepository(VmSnapshot snapshot) : IVmRepository
    {
        public int RemovalCount { get; private set; }
        public Task<VmLibrary> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(new VmLibrary([snapshot], [], []));
        public Task<VmSnapshot> CreateAsync(VmConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VmSnapshot> SaveAsync(VmConfiguration configuration, string expectedRevision, CancellationToken cancellationToken = default) => throw new ConfigurationConflictException("Concurrent edit");
        public Task RegisterAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) { RemovalCount++; return Task.CompletedTask; }
    }
}
