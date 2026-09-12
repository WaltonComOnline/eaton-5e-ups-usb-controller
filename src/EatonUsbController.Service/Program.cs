using EatonUsbController.Service;

var builder = Host.CreateApplicationBuilder(args);

// Windows Service support
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EatonUsbController";
});

// Register all EatonUsbController background services
HostConfigurator.ConfigureServices(builder);

var host = builder.Build();
host.Run();
