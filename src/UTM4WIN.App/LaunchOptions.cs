namespace UTM4WIN.App;

public static class LaunchOptions {
    public static string? ParseLibraryRoot(string[] arguments) {
        if (arguments.Length == 0)
            return null;
        if (arguments.Length != 2 || arguments[0] != "--library-root" || string.IsNullOrWhiteSpace(arguments[1]))
            throw new ArgumentException("Supported option: --library-root <absolute local directory>.");
        return arguments[1];
    }
}