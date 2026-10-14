using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UTM4WIN.App;

public partial class App : Microsoft.UI.Xaml.Application {
    private ServiceProvider? _services;
    private Window? _window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        try {
            _services = Composition.CreateServices(LaunchOptions.ParseLibraryRoot(Environment.GetCommandLineArgs().Skip(1).ToArray()));
            var logger = _services.GetRequiredService<ILogger<App>>();
            _window = _services.GetRequiredService<MainWindow>();
            _window.Closed += async (_, _) => {
                logger.LogInformation("Application shell closed.");
                await _services.DisposeAsync();
                _services = null;
                _window = null;
            };
            _window.Activate();
            logger.LogInformation("Application shell started.");
        } catch (Exception error) {
            Trace.TraceError("Application startup failed: {0}", error);
            if (_services is not null)
                await _services.DisposeAsync();
            _services = null;
            Environment.ExitCode = 1;
            _window = new Window {
                Title = "UTM4WIN",
                Content = new TextBlock {
                    Text = "UTM4WIN could not start. Close this window and check the debugger output.",
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            _window.Activate();
        }
    }
}

