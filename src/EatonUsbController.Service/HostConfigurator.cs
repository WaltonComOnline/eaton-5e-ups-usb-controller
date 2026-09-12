using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using EatonUsbController.Core;
using EatonUsbController.Core.Models;

namespace EatonUsbController.Service;

/// <summary>
/// Registers all EatonUsbController background services and their dependencies.
/// </summary>
public static class HostConfigurator
{
    public static void ConfigureServices(HostApplicationBuilder builder)
    {
        // Configuration
        builder.Services.Configure<AppConfig>(
            builder.Configuration.GetSection("EatonUsbController"));
        builder.Services.AddSingleton(sp => new ConfigurationStore(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(AppPaths.NutEtc, "upsd.users"),
            sp.GetRequiredService<ILogger<ConfigurationStore>>()));

        // Core services
        builder.Services.AddSingleton<NutClient>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue.Nut;
            var logger = sp.GetRequiredService<ILogger<NutClient>>();
            return new NutClient(config.Host, config.Port, logger);
        });

        // Application services
        builder.Services.AddSingleton<EventLogger>();
        builder.Services.AddSingleton<EventBus>();
        builder.Services.AddHostedService<NutHostedService>();
        builder.Services.AddHostedService<UpsMonitor>();
        builder.Services.AddHostedService<BeeperManager>();
        builder.Services.AddHostedService<ShutdownManager>();
        builder.Services.AddHostedService<AlertEngine>();
        builder.Services.AddHostedService<ScriptExecutor>();
        builder.Services.AddSingleton<NutHealthMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NutHealthMonitor>());
        builder.Services.AddHostedService<PipeServer>();
    }
}
