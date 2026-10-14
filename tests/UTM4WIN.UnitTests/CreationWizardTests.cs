using Microsoft.Extensions.Logging.Abstractions;
using UTM4WIN.App.ViewModels;
using UTM4WIN.Application;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class CreationWizardTests {
    [TestMethod]
    public async Task StepsValidateInputAndRequireCleanupConsent() {
        var service = new TestCreation();
        var wizard = Create(service);
        Assert.IsFalse(wizard.Next());
        wizard.Name = "Linux";
        Assert.IsTrue(wizard.Next());
        Assert.IsFalse(wizard.Next());
        wizard.IsoPath = @"C:\Media\debian.iso";
        wizard.QemuImgPath = @"C:\Qemu\qemu-img.exe";
        wizard.DiskSizeGiB = 1.5;
        Assert.IsFalse(wizard.Next());
        wizard.DiskSizeGiB = 32;
        Assert.IsTrue(wizard.Next());
        Assert.IsFalse(await wizard.CreateAsync());
        Assert.AreEqual(0, service.Calls);
        wizard.DiscardIncompleteDisk = true;
        Assert.IsTrue(await wizard.CreateAsync());
        Assert.IsNotNull(wizard.CreatedMachine);
        Assert.IsFalse(await wizard.CreateAsync());
        Assert.AreEqual(1, service.Calls);
    }

    [TestMethod]
    public async Task CancelWaitsForServiceCleanupAndKeepsDraft() {
        var entered = new TaskCompletionSource();
        var service = new TestCreation {
            Run = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
        };
        var wizard = Ready(service);
        var creation = wizard.CreateAsync();
        await entered.Task;
        Assert.IsTrue(wizard.IsCreating);
        Assert.IsFalse(wizard.CanEdit);
        wizard.Back();
        Assert.AreEqual(2, wizard.Step);
        wizard.CancelCreationCommand.Execute(null);
        Assert.IsFalse(await creation);
        Assert.IsFalse(wizard.IsCreating);
        Assert.IsNull(wizard.CreatedMachine);
        Assert.AreEqual("Linux", wizard.Name);
        Assert.IsTrue(wizard.HasChanges);
        Assert.IsTrue(wizard.Error.StartsWith("Creation canceled", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CreationFailureKeepsInputsAndAllowsCorrection() {
        var wizard = Ready(new TestCreation { Run = (_, _) => throw new IOException("Injected disk full") });
        Assert.IsFalse(await wizard.CreateAsync());
        Assert.AreEqual("Injected disk full", wizard.Error);
        Assert.AreEqual(@"C:\Media\debian.iso", wizard.IsoPath);
        Assert.IsTrue(wizard.CanEdit);
        wizard.Back();
        Assert.AreEqual(1, wizard.Step);
    }

    private static CreationWizardViewModel Create(TestCreation service) => new(service, new QemuToolSelection(), NullLogger<CreationWizardViewModel>.Instance);
    private static CreationWizardViewModel Ready(TestCreation service) {
        var wizard = Create(service);
        wizard.Name = "Linux";
        wizard.Next();
        wizard.IsoPath = @"C:\Media\debian.iso";
        wizard.QemuImgPath = @"C:\Qemu\qemu-img.exe";
        wizard.Next();
        wizard.DiscardIncompleteDisk = true;
        return wizard;
    }
    private sealed class TestCreation : IVmCreationService {
        public int Calls {
            get; private set;
        }
        public Func<VmCreationRequest, CancellationToken, Task<VmSnapshot>> Run { get; init; } = (request, _) => Task.FromResult(new VmSnapshot(request.Configuration, "revision"));
        public Task<VmSnapshot> CreateAsync(VmCreationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) {
            Calls++;
            return Run(request, cancellationToken);
        }
    }
}
