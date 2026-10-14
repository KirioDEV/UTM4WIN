using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UTM4WIN.Application;
using UTM4WIN.Virtualization.Qemu;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class DiskToolTests
{
    [TestMethod]
    public void WindowsArgumentEncodingRoundTripsSpacesQuotesUnicodeAndTrailingSlashes()
    {
        string[] arguments = ["tool.exe", "", "plain", "space comma, café", @"C:\with spaces\", "quote\"inside", "slashes\\\"quote"];
        string line = string.Join(" ", arguments.Select(OwnedToolProcess.Quote));
        nint pointer = CommandLineToArgvW(line, out int count);
        Assert.AreNotEqual(nint.Zero, pointer);
        try
        {
            var decoded = Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * nint.Size))!).ToArray();
            CollectionAssert.AreEqual(arguments, decoded);
        }
        finally { LocalFree(pointer); }
        Assert.Throws<ArgumentException>(() => OwnedToolProcess.Quote("bad\0argument"));
    }

    [TestMethod]
    [DataRow("{\"format\":\"raw\",\"virtual-size\":1073741824}")]
    [DataRow("{\"format\":\"qcow2\",\"virtual-size\":1}")]
    [DataRow("{\"format\":\"qcow2\",\"virtual-size\":1073741824,\"backing-filename\":\"base.qcow2\"}")]
    [DataRow("{\"format\":\"qcow2\",\"virtual-size\":1073741824,\"format-specific\":{\"data\":{\"data-file\":\"external.raw\"}}}")]
    [DataRow("{\"format\":\"qcow2\",\"virtual-size\":1073741824,\"encrypted\":true}")]
    [DataRow("{\"format\":\"qcow2\",\"virtual-size\":1073741824,\"format-specific\":{\"data\":{\"corrupt\":true}}}")]
    [DataRow("{}")]
    [DataRow("[]")]
    public void UnexpectedImageMetadataIsRejected(string json) => Assert.Throws<ConfigurationException>(() => QemuImgDiskService.ValidateInfo(json, 1073741824));

    [TestMethod]
    public async Task MissingOrUnqualifiedExecutableCannotTouchDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "UTM4WIN-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "qemu-img.exe");
        string disk = Path.Combine(root, "sentinel.qcow2");
        try
        {
            await File.WriteAllTextAsync(disk, "preserve");
            await Assert.ThrowsAsync<FileNotFoundException>(() => new QemuImgDiskService().CreateAsync(executable, disk, 1073741824));
            await File.WriteAllTextAsync(executable, "not executable, must never be run");
            await Assert.ThrowsAsync<ConfigurationException>(() => new QemuImgDiskService().CreateAsync(executable, disk, 1073741824));
            Assert.AreEqual("preserve", await File.ReadAllTextAsync(disk));
        }
        finally
        {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(root).StartsWith("UTM4WIN-tool-", StringComparison.Ordinal)) Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint handle);
}
