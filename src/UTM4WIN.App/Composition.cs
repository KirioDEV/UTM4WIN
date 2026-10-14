using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UTM4WIN.App.ViewModels;

namespace UTM4WIN.App;

public static class Composition
{
    public static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

