using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UTM4WIN.App.ViewModels;
using UTM4WIN.Application;
using UTM4WIN.Infrastructure;

namespace UTM4WIN.App;

public static class Composition
{
    public static ServiceProvider CreateServices(string? libraryRoot = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IVmRepository>(_ => new JsonVmRepository(libraryRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UTM4WIN", "Library")));
        services.AddSingleton<LibraryDialogs>();
        services.AddSingleton<ILibraryDialogs>(provider => provider.GetRequiredService<LibraryDialogs>());
        services.AddSingleton<LibraryViewModel>();
        services.AddTransient<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

