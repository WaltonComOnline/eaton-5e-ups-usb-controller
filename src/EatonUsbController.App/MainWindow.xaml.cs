using System.ComponentModel;
using System.Windows;
using EatonUsbController.App.ViewModels;

namespace EatonUsbController.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();

        // Set tray icon from the .exe's PE icon resource — this bypasses
        // Hardcodet's ImageSource→Icon conversion (silently broken on .NET 10)
        // and avoids WPF pack-URI resource loading which can also fail for .ico
        // in SDK-style projects. ExtractAssociatedIcon always works because
        // <ApplicationIcon> embeds the icon in the PE header.
        TrayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(
            Environment.ProcessPath!)!;

        _vm = (MainViewModel)DataContext;
        _vm.ShowWindowRequested += () =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
    }

    // Navigation click handlers
    private void NavDashboard_Click(object sender, RoutedEventArgs e) => _vm.NavigateTo("Dashboard");
    private void NavBeeper_Click(object sender, RoutedEventArgs e) => _vm.NavigateTo("Beeper");
    private void NavEvents_Click(object sender, RoutedEventArgs e) => _vm.NavigateTo("Events");
    private void NavShutdown_Click(object sender, RoutedEventArgs e) => _vm.NavigateTo("Shutdown");
    private void NavAlerts_Click(object sender, RoutedEventArgs e) => _vm.NavigateTo("Alerts");
    private void NavScripts_Click(object sender, RoutedEventArgs e) => _vm.NavigateTo("Scripts");
    private void NavNut_Click(object sender, RoutedEventArgs e) => _vm.NavigateTo("NUT");

    private void Window_StateChanged(object sender, System.EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        TrayIcon.Dispose();
        base.OnClosing(e);
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }
}