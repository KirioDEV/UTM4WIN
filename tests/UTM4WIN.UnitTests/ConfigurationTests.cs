using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public void ConfigurationRoundTripPreservesAllHardwareAndOwnershipFields()
    {
        var configuration = VmConfiguration.CreateMetadata("Debian") with
        {
            ExecutionMode = ExecutionMode.Tcg, CpuCount = 4, MemoryMiB = 4096, Network = NetworkMode.User,
            Storage = [new() { Id = Guid.NewGuid(), Path = "disks/system.qcow2", Ownership = ResourceOwnership.Owned, Format = DiskFormat.Qcow2, IsReadOnly = false },
                new() { Id = Guid.NewGuid(), Path = @"C:\Media\debian.iso", Ownership = ResourceOwnership.External, Format = DiskFormat.Iso, IsReadOnly = true }]
        };
        byte[] bytes = ConfigurationJson.Serialize(configuration);
        var loaded = ConfigurationJson.Deserialize(bytes);
        Assert.AreEqual(configuration with { Storage = loaded.Configuration.Storage }, loaded.Configuration);
        CollectionAssert.AreEqual(configuration.Storage.ToArray(), loaded.Configuration.Storage.ToArray());
        Assert.AreEqual(64, loaded.Revision.Length);
    }

    [TestMethod]
    [DataRow("CON")]
    [DataRow("nul.txt")]
    [DataRow("LPT¹")]
    [DataRow("Debian.")]
    [DataRow(" Debian")]
    [DataRow("A/B")]
    public void ReservedOrAmbiguousNamesAreRejected(string name) =>
        Assert.Throws<ConfigurationException>(() => ConfigurationJson.Serialize(VmConfiguration.CreateMetadata(name)));

    [TestMethod]
    [DataRow("disks/../escape.qcow2")]
    [DataRow("disks/CON.qcow2")]
    [DataRow("disks/system.qcow2:stream")]
    [DataRow("disks\\system.qcow2")]
    [DataRow("C:/disks/system.qcow2")]
    public void OwnedPathsRejectTraversalAliasesAndStreams(string path) => Assert.IsFalse(ConfigurationValidator.IsOwnedPath(path));

    [TestMethod]
    public void InvalidHardwareAndDuplicateResourcesAreRejected()
    {
        var resource = new StorageResource { Id = Guid.NewGuid(), Path = "disks/system.qcow2", Format = DiskFormat.Qcow2, Ownership = ResourceOwnership.Owned, IsReadOnly = false };
        var configuration = VmConfiguration.CreateMetadata("Debian") with
        {
            CpuCount = 0, MemoryMiB = -1, ExecutionMode = (ExecutionMode)999, Storage = [resource, resource]
        };
        var issues = ConfigurationValidator.Validate(configuration);
        foreach (string field in new[] { "CpuCount", "MemoryMiB", "ExecutionMode", "Storage.Id", "Storage.Path" })
            Assert.IsTrue(issues.Any(issue => issue.Field == field), field);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(2)]
    public void UnsupportedSchemasFailWithoutModifyingInput(int schema)
    {
        string json = Encoding.UTF8.GetString(ConfigurationJson.Serialize(VmConfiguration.CreateMetadata("Debian")))
            .Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {schema}");
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] original = bytes.ToArray();
        Assert.Throws<ConfigurationException>(() => ConfigurationJson.Deserialize(bytes));
        CollectionAssert.AreEqual(original, bytes);
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":1,\"schemaVersion\":2}")]
    [DataRow("{\"schemaVersion\":1}")]
    [DataRow("{\"schemaVersion\":1,\"configuration\":null}")]
    [DataRow("[]")]
    public void MalformedDocumentsFailClearly(string json) =>
        Assert.Throws<ConfigurationException>(() => ConfigurationJson.Deserialize(Encoding.UTF8.GetBytes(json)));

    [TestMethod]
    public void UnknownPropertiesAndIntegerEnumsAreRejected()
    {
        string json = Encoding.UTF8.GetString(ConfigurationJson.Serialize(VmConfiguration.CreateMetadata("Debian")));
        foreach (string invalid in new[] { json.Replace("\"cpuCount\": 2", "\"cpuCount\": 2, \"extraArgs\": \"-evil\""), json.Replace("\"executionMode\": \"Whpx\"", "\"executionMode\": 0") })
            Assert.Throws<ConfigurationException>(() => ConfigurationJson.Deserialize(Encoding.UTF8.GetBytes(invalid)));
    }

    [TestMethod]
    public void DraftChangesAreIsolatedAndCanBeDiscarded()
    {
        var original = VmConfiguration.CreateMetadata("Original");
        var draft = new VmDraft(new(original, "revision"));
        draft.Configuration = draft.Configuration with { Name = "Edited" };
        Assert.IsTrue(draft.HasChanges);
        Assert.AreEqual("Original", original.Name);
        draft.Discard();
        Assert.IsFalse(draft.HasChanges);
        Assert.AreSame(original, draft.Configuration);
    }
}
