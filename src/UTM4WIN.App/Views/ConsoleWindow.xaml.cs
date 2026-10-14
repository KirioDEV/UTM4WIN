using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using UTM4WIN.Application;

namespace UTM4WIN.App.Views;

public sealed partial class ConsoleWindow : Window {
    private readonly IVmDisplaySession _display;
    private readonly DispatcherQueueTimer _timer;
    private readonly HashSet<uint> _held = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WriteableBitmap? _bitmap;
    private long _sequence = -1;
    private bool _closing;
    private bool _allowClose;
    private bool _connecting;
    private bool _painting;
    private string _actionError = "";
    public IVmDisplaySession Display => _display;
    private readonly HashSet<double> _observedScales = [];

    public ConsoleWindow(string name, IVmDisplaySession display, ElementTheme theme) {
        _display = display;
        InitializeComponent();
        Title = name + " — UTM4WIN";
        Root.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme == ElementTheme.Dark ? Microsoft.UI.Windowing.TitleBarTheme.Dark : Microsoft.UI.Windowing.TitleBarTheme.Light;
        AppWindow.Resize(new SizeInt32(1024, 760));
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += OnTick;
        _timer.Start();
        Activated += async (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) await ReleaseAsync(); };
        AppWindow.Closing += async (_, args) => {
            if (_allowClose)
                return;
            args.Cancel = true;
            if (_closing)
                return;
            _closing = true;
            _timer.Stop();
            await ReleaseAsync();
            try {
                await _display.DisconnectAsync();
            } catch (Exception error) { ConnectionStatus.Text = "Display disconnect: " + error.Message; }
            _allowClose = true;
            Close();
        };
        Closed += (_, _) => { _timer.Stop(); _timer.Tick -= OnTick; GuestImage.Source = null; _bitmap = null; _closed.TrySetResult(); };
        Root.Loaded += async (_, _) => { if (!_display.IsConnected) await ReconnectAsync(); };
    }

    public Task CloseConsoleAsync() {
        Close();
        return _closed.Task;
    }

    private void Input(Action action) {
        if (_closing || _connecting || !_display.IsConnected)
            return;
        try {
            action();
        } catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) {
            ConnectionStatus.Text = "Input unavailable: " + error.Message;
            _held.Clear();
        }
    }

    private async void OnTick(DispatcherQueueTimer sender, object args) {
        if (_painting || _closing)
            return;
        _painting = true;
        try {
            var frame = _display.ReadFrame();
            if (Root.XamlRoot is { } root)
                _observedScales.Add(Math.Round(root.RasterizationScale, 3));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(Surface,
                "Scales " + string.Join(";", _observedScales.Order().Select(scale => scale.ToString(System.Globalization.CultureInfo.InvariantCulture))) + $"; held keys {_held.Count}");
            ReconnectButton.IsEnabled = !_connecting && _display.CanReconnect;
            if (frame is null) {
                GuestImage.Source = null;
                _bitmap = null;
                _sequence = -1;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(GuestImage, "");
                ConnectionStatus.Text = !_display.CanReconnect ? "Console closed. Check the VM state in your library." : _connecting ? "Connecting display…" : _display.IsConnected ? "Waiting for a guest image…" : "Display disconnected. The VM may still be running. " + (_display.Error.Length > 0 ? _display.Error : _actionError);
                return;
            }
            ConnectionStatus.Text = $"Connected · {frame.Width} × {frame.Height}";
            if (frame.Sequence == _sequence)
                return;
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height) {
                _bitmap = new(frame.Width, frame.Height);
                GuestImage.Source = _bitmap;
            }
            using var pixels = _bitmap.PixelBuffer.AsStream();
            await pixels.WriteAsync(frame.Pixels);
            _bitmap.Invalidate();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(GuestImage, $"Connection {_display.ConnectionGeneration}, frame {frame.Sequence}");
            _sequence = frame.Sequence;
        } catch (Exception error) { ConnectionStatus.Text = "Display error: " + error.Message; GuestImage.Source = null; } finally { _painting = false; }
    }

    private async Task ReleaseAsync() {
        _held.Clear();
        Surface.ReleasePointerCaptures();
        try {
            await _display.ReleaseInputAsync();
        } catch (Exception error) { ConnectionStatus.Text = "Input release unavailable: " + error.Message; }
    }
    private async void OnReleaseInput(object sender, RoutedEventArgs args) => await ReleaseAsync();
    private async void OnLostFocus(object sender, RoutedEventArgs args) => await ReleaseAsync();
    private async void OnPointerCanceled(object sender, PointerRoutedEventArgs args) => await ReleaseAsync();
    private async void OnReconnect(object sender, RoutedEventArgs args) => await ReconnectAsync();
    private async Task ReconnectAsync() {
        if (_connecting || _closing)
            return;
        _connecting = true;
        ReconnectButton.IsEnabled = false;
        ConnectionStatus.Text = "Connecting display…";
        _actionError = "";
        GuestImage.Source = null;
        _bitmap = null;
        _sequence = -1;
        await ReleaseAsync();
        try {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _display.ReconnectAsync(deadline.Token);
        } catch (Exception error) { _actionError = "Reconnect failed: " + error.Message; ConnectionStatus.Text = _actionError; } finally { _connecting = false; }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args) {
        uint? symbol = KeySymbol(args.Key);
        if (symbol is null)
            return;
        if (args.Key == VirtualKey.Escape && _held.Contains(0xffe3) && _held.Contains(0xffe9)) {
            _ = ReleaseAsync();
            args.Handled = true;
            return;
        }
        Input(() => { _display.KeyDown(symbol.Value); _held.Add(symbol.Value); });
        args.Handled = true;
    }
    private void OnKeyUp(object sender, KeyRoutedEventArgs args) {
        if (KeySymbol(args.Key) is not uint symbol)
            return;
        Input(() => _display.KeyUp(symbol));
        _held.Remove(symbol);
        args.Handled = true;
    }
    private static uint? KeySymbol(VirtualKey key) => key switch {
        VirtualKey.Enter => 0xff0d,
        VirtualKey.Tab => 0xff09,
        VirtualKey.Back => 0xff08,
        VirtualKey.Escape => 0xff1b,
        VirtualKey.Left => 0xff51,
        VirtualKey.Up => 0xff52,
        VirtualKey.Right => 0xff53,
        VirtualKey.Down => 0xff54,
        VirtualKey.Home => 0xff50,
        VirtualKey.End => 0xff57,
        VirtualKey.PageUp => 0xff55,
        VirtualKey.PageDown => 0xff56,
        VirtualKey.Insert => 0xff63,
        VirtualKey.Delete => 0xffff,
        VirtualKey.Shift => 0xffe1,
        VirtualKey.Control => 0xffe3,
        VirtualKey.Menu => 0xffe9,
        VirtualKey.LeftShift => 0xffe1,
        VirtualKey.RightShift => 0xffe2,
        VirtualKey.LeftControl => 0xffe3,
        VirtualKey.RightControl => 0xffe4,
        VirtualKey.LeftMenu => 0xffe9,
        VirtualKey.RightMenu => 0xffea,
        VirtualKey.CapitalLock => 0xffe5,
        VirtualKey.NumberKeyLock => 0xff7f,
        VirtualKey.Multiply => 42,
        VirtualKey.Add => 43,
        VirtualKey.Subtract => 45,
        VirtualKey.Decimal => 46,
        VirtualKey.Divide => 47,
        (VirtualKey)186 => 59,
        (VirtualKey)187 => 61,
        (VirtualKey)188 => 44,
        (VirtualKey)189 => 45,
        (VirtualKey)190 => 46,
        (VirtualKey)191 => 47,
        (VirtualKey)192 => 96,
        (VirtualKey)219 => 91,
        (VirtualKey)220 => 92,
        (VirtualKey)221 => 93,
        (VirtualKey)222 => 39,
        VirtualKey.Space => 32,
        >= VirtualKey.A and <= VirtualKey.Z => (uint)key + 32,
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => (uint)key,
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => 48 + (uint)(key - VirtualKey.NumberPad0),
        >= VirtualKey.F1 and <= VirtualKey.F12 => 0xffbe + (uint)(key - VirtualKey.F1),
        _ => null
    };

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args) {
        Surface.Focus(FocusState.Pointer);
        Surface.CapturePointer(args.Pointer);
        SendPointer(args);
    }
    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) => SendPointer(args);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs args) {
        SendPointer(args);
        Surface.ReleasePointerCapture(args.Pointer);
    }
    private void SendPointer(PointerRoutedEventArgs args) {
        var frame = _display.ReadFrame();
        if (frame is null)
            return;
        var point = args.GetCurrentPoint(Surface);
        var mapped = MapPointer(point.Position.X, point.Position.Y, Surface.ActualWidth, Surface.ActualHeight, frame.Width, frame.Height);
        if (mapped is null) {
            _ = ReleaseAsync();
            return;
        }
        byte buttons = (byte)((point.Properties.IsLeftButtonPressed ? 1 : 0) | (point.Properties.IsMiddleButtonPressed ? 2 : 0) | (point.Properties.IsRightButtonPressed ? 4 : 0));
        Input(() => _display.Pointer(mapped.Value.X, mapped.Value.Y, buttons));
        args.Handled = true;
    }
    public static (ushort X, ushort Y)? MapPointer(double x, double y, double width, double height, int guestWidth, int guestHeight) {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || guestWidth is < 1 or > 2048 || guestHeight is < 1 or > 2048)
            return null;
        double scale = Math.Min(width / guestWidth, height / guestHeight);
        double gx = (x - (width - guestWidth * scale) / 2) / scale, gy = (y - (height - guestHeight * scale) / 2) / scale;
        return gx < 0 || gy < 0 || gx >= guestWidth || gy >= guestHeight ? null : ((ushort)Math.Floor(gx), (ushort)Math.Floor(gy));
    }
}
