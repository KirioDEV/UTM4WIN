using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UTM4WIN.App;
using UTM4WIN.App.ViewModels;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class ShellTests
{
    [TestMethod]
    public void NavigationNotifiesDependentBindingsAndIgnoresRepeatedSelection()
    {
        var model = new ShellViewModel(NullLogger<ShellViewModel>.Instance);
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        model.Section = ShellSection.Settings;
        Assert.IsTrue(model.IsSettingsSelected);
        Assert.IsFalse(model.IsLibrarySelected);
        Assert.AreEqual("Settings", model.SectionTitle);
        CollectionAssert.AreEquivalent(new[] { "Section", "SectionTitle", "IsLibrarySelected", "IsSettingsSelected" }, changed);
        changed.Clear();
        model.Section = ShellSection.Settings;
        Assert.AreEqual(0, changed.Count);
    }

    [TestMethod]
    public void InvalidThemeDoesNotCorruptExistingState()
    {
        var model = new ShellViewModel(NullLogger<ShellViewModel>.Instance) { Theme = AppTheme.Dark };
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Theme = (AppTheme)99);
        Assert.AreEqual(AppTheme.Dark, model.Theme);
    }

    [TestMethod]
    public void CompositionResolvesOneShellStateAndLogging()
    {
        using var services = Composition.CreateServices();
        Assert.AreSame(services.GetRequiredService<ShellViewModel>(), services.GetRequiredService<ShellViewModel>());
        Assert.IsNotNull(services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ShellViewModel>>());
    }

    [TestMethod]
    public void DomainAndApplicationHaveNoUiOrBackendDependency()
    {
        foreach (string name in new[] { "UTM4WIN.Domain", "UTM4WIN.Application" })
        {
            var references = Assembly.Load(name).GetReferencedAssemblies().Select(reference => reference.Name!);
            Assert.IsFalse(references.Any(reference => reference.Contains("WinUI") || reference.Contains("Microsoft.UI") ||
                reference.StartsWith("UTM4WIN.App", StringComparison.Ordinal) && reference != "UTM4WIN.Application" ||
                reference.StartsWith("UTM4WIN.Virtualization", StringComparison.Ordinal)));
        }
    }
}

