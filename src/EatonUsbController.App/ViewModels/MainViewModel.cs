using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EatonUsbController.App.Services;
using EatonUsbController.Core;
using EatonUsbController.Core.Contracts;
using EatonUsbController.Core.Models;

namespace EatonUsbController.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PipeClient _pipeClient;
    private readonly DispatcherTimer _reconnectTimer;
    private AppConfig? _loadedConfig;

    public event Action? ShowWindowRequested;

    // --- Status ---
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private string _upsState = "Unknown";
    [ObservableProperty] private string _rawStatus = "";
    [ObservableProperty] private double _batteryCharge;
    [ObservableProperty] private double _batteryRuntime;
    [ObservableProperty] private double _inputVoltage;
    [ObservableProperty] private double _outputVoltage;
    [ObservableProperty] private double _load;
    [ObservableProperty] private string _beeperStatus = "Unknown";
    [ObservableProperty] private string _manufacturer = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _serial = "";
    [ObservableProperty] private string _firmware = "";
    [ObservableProperty] private double _powerNominal;
    [ObservableProperty] private double _outputFrequencyNominal;
    [ObservableProperty] private double _outputVoltageNominal;
    [ObservableProperty] private string _outletStatus = "";
    [ObservableProperty] private double _batteryChargeLow;
    [ObservableProperty] private string _batteryType = "";

    // --- UI State ---
    [ObservableProperty] private string _statusColor = "Gray";
    [ObservableProperty] private string _selectedPage = "Dashboard";
    [ObservableProperty] private string _trayTooltip = "Eaton 5E Controller — Disconnected";
    [ObservableProperty] private string _selectedCommand = "";
    [ObservableProperty] private string _commandResult = "";
    // --- App Info ---
    public string AppVersion { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string AppAuthor { get; } = "WaltonComOnline";
    public string AppTitle { get; } = "Eaton 5E Controller";

    // --- Power flow state for synoptic ---
    [ObservableProperty] private string _powerSourceLabel = "AC Power";
    [ObservableProperty] private string _powerFlowColor = "#22C55E";
    [ObservableProperty] private bool _onBattery;
    // --- Config State ---
    [ObservableProperty] private bool _configLoaded;
    [ObservableProperty] private string _configStatus = "";

    // Beeper config
    [ObservableProperty] private string _selectedBeeperMode = "AlwaysOn";

    // Shutdown config
    [ObservableProperty] private bool _shutdownEnabled;
    [ObservableProperty] private int _shutdownBatteryThreshold = 20;
    [ObservableProperty] private int _shutdownRuntimeThreshold = 120;
    [ObservableProperty] private int _shutdownGracePeriod = 30;
    [ObservableProperty] private bool _shutdownUseHibernate;
    [ObservableProperty] private string _shutdownCustomCommand = "";

    // Alert config
    [ObservableProperty] private bool _emailEnabled;
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private int _smtpPort = 587;
    [ObservableProperty] private bool _smtpUseTls = true;
    [ObservableProperty] private string _smtpUsername = "";
    [ObservableProperty] private string _smtpPassword = "";
    [ObservableProperty] private string _emailFrom = "";
    [ObservableProperty] private string _emailTo = "";
    [ObservableProperty] private string _webhookUrlsText = "";

    // NUT info
    [ObservableProperty] private bool _nutDriverRunning;
    [ObservableProperty] private bool _nutUpsdRunning;
    [ObservableProperty] private bool _nutServiceConnected;
    [ObservableProperty] private string _nutHost = "";
    [ObservableProperty] private int _nutPort;
    [ObservableProperty] private string _nutUpsName = "";
    [ObservableProperty] private string _nutUsername = "";
    [ObservableProperty] private string _nutPassword = "";
    [ObservableProperty] private string _nutVariableName = "";
    [ObservableProperty] private string _nutVariableValue = "";

    // --- Collections ---
    public ObservableCollection<PowerEvent> RecentEvents { get; } = [];
    public ObservableCollection<string> AvailableCommands { get; } = [];
    public ObservableCollection<KeyValuePair<string, string>> WritableVariables { get; } = [];
    public ObservableCollection<ScheduleEntryViewModel> BeeperSchedule { get; } = [];
    public ObservableCollection<EventTypeToggle> AlertEventTypes { get; } = [];
    public ObservableCollection<ScriptTriggerViewModel> ScriptTriggerList { get; } = [];

    // --- Enum arrays for ComboBox binding ---
    public string[] BeeperModes { get; } = Enum.GetNames<BeeperMode>();
    public string[] PowerEventTypeNames { get; } = Enum.GetNames<PowerEventType>();
    public DayOfWeek[] DaysOfWeek { get; } = Enum.GetValues<DayOfWeek>();

    public MainViewModel()
    {
        _pipeClient = new PipeClient();
        _pipeClient.OnStatusUpdate += OnStatusUpdate;
        _pipeClient.OnEvent += OnEvent;
        _pipeClient.OnConnectionChanged += connected =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = connected;
                ConnectionStatus = connected ? "Connected" : "Disconnected";
                if (connected)
                {
                    if (!ConfigLoaded)
                        _ = LoadConfigAsync();
                    _ = LoadNutInfoAsync();
                }
                else
                {
                    NutDriverRunning = false;
                    NutUpsdRunning = false;
                    NutServiceConnected = false;
                }
            });
        };

        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reconnectTimer.Tick += async (_, _) =>
        {
            await TryConnectAsync();
            if (IsConnected)
                await LoadNutInfoAsync();
        };
        _reconnectTimer.Start();

        _ = TryConnectAsync();
    }

    private async Task TryConnectAsync()
    {
        if (IsConnected) return;

        try
        {
            ConnectionStatus = "Connecting...";
            await _pipeClient.ConnectAsync();
        }
        catch
        {
            ConnectionStatus = "Service unavailable";
        }
    }

    private void OnStatusUpdate(UpsStatus status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpsState = status.State.ToString();
            RawStatus = status.RawStatus;
            BatteryCharge = status.BatteryCharge;
            BatteryRuntime = status.BatteryRuntime;
            InputVoltage = status.InputVoltage;
            OutputVoltage = status.OutputVoltage;
            Load = status.Load;
            BeeperStatus = status.BeeperStatus.ToString();
            Manufacturer = status.Manufacturer;
            Model = status.Model;
            Serial = status.Serial;
            Firmware = status.Firmware;
            PowerNominal = status.PowerNominal;
            OutputFrequencyNominal = status.OutputFrequencyNominal;
            OutputVoltageNominal = status.OutputVoltageNominal;
            OutletStatus = status.OutletStatus;
            BatteryChargeLow = status.BatteryChargeLow;
            BatteryType = status.BatteryType;

            StatusColor = status.State switch
            {
                EatonUsbController.Core.Models.UpsState.Online or EatonUsbController.Core.Models.UpsState.Charging => "#22C55E",
                EatonUsbController.Core.Models.UpsState.OnBattery or EatonUsbController.Core.Models.UpsState.Discharging => "#EAB308",
                EatonUsbController.Core.Models.UpsState.LowBattery or EatonUsbController.Core.Models.UpsState.ForcedShutdown => "#EF4444",
                _ => "#6B7280"
            };

            // Synoptic power flow state
            OnBattery = status.State is EatonUsbController.Core.Models.UpsState.OnBattery
                     or EatonUsbController.Core.Models.UpsState.Discharging
                     or EatonUsbController.Core.Models.UpsState.LowBattery;
            PowerSourceLabel = OnBattery ? "Battery" : "AC Power";
            PowerFlowColor = StatusColor;

            var runtimeMin = status.BatteryRuntime / 60;
            TrayTooltip = $"Eaton 5E — {status.State}\n" +
                          $"Battery: {status.BatteryCharge:F0}% | {runtimeMin:F0}min\n" +
                          $"Load: {status.Load:F0}% | Input: {status.InputVoltage:F0}V";
        });
    }

    private void OnEvent(PowerEvent evt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecentEvents.Insert(0, evt);
            while (RecentEvents.Count > 200)
                RecentEvents.RemoveAt(RecentEvents.Count - 1);

            // Update UI immediately when UPS disconnects/reconnects
            if (evt.EventType == PowerEventType.UpsDisconnected)
            {
                UpsState = "Disconnected";
                StatusColor = "#6B7280";
                BeeperStatus = "Unknown";
                TrayTooltip = "Eaton 5E Controller — UPS Disconnected";
            }
            else if (evt.EventType == PowerEventType.UpsConnected)
            {
                TrayTooltip = $"Eaton 5E Controller — {evt.Details}";
            }
        });
    }

    // ===================== COMMANDS =====================

    [RelayCommand]
    private async Task ToggleBeeperAsync()
    {
        try
        {
            var command = BeeperStatus == "Enabled" ? "beeper.disable" : "beeper.enable";
            await _pipeClient.SendCommandAsync(command);
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private void ShowWindow() => ShowWindowRequested?.Invoke();

    [RelayCommand]
    private async Task SendCommandAsync(string? command)
    {
        if (string.IsNullOrEmpty(command)) return;
        try
        {
            await _pipeClient.SendCommandAsync(command);
            CommandResult = $"OK: {command}";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task LoadNutCommandsAsync()
    {
        try
        {
            var response = await _pipeClient.SendRequestAsync<ListNutCommandsResponse>(
                new ListNutCommandsRequest());
            AvailableCommands.Clear();
            foreach (var cmd in response.Commands)
                AvailableCommands.Add(cmd);
        }
        catch (Exception ex) { CommandResult = $"Error loading commands: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task LoadWritableVarsAsync()
    {
        try
        {
            var response = await _pipeClient.SendRequestAsync<ListWritableVarsResponse>(
                new ListWritableVarsRequest());
            WritableVariables.Clear();
            foreach (var kv in response.Variables)
                WritableVariables.Add(kv);
        }
        catch (Exception ex) { CommandResult = $"Error loading variables: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task TestBatteryQuickAsync()
    {
        try
        {
            await _pipeClient.SendCommandAsync("test.battery.start.quick");
            CommandResult = "\u2713 Quick battery test started";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task TestBatteryDeepAsync()
    {
        try
        {
            await _pipeClient.SendCommandAsync("test.battery.start.deep");
            CommandResult = "\u2713 Deep battery test started";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task StopBatteryTestAsync()
    {
        try
        {
            await _pipeClient.SendCommandAsync("test.battery.stop");
            CommandResult = "\u2713 Battery test stopped";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task MuteBeeperAsync()
    {
        try
        {
            await _pipeClient.SendCommandAsync("beeper.mute");
            CommandResult = "\u2713 Beeper muted";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ResetInputMinMaxAsync()
    {
        try
        {
            await _pipeClient.SendCommandAsync("reset.input.minmax");
            CommandResult = "\u2713 Input voltage min/max reset";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RestartNutAsync()
    {
        try
        {
            CommandResult = "Restarting NUT processes...";
            var response = await _pipeClient.SendRequestAsync<CommandResponse>(
                new RestartNutRequest());
            CommandResult = response.Success
                ? "\u2713 NUT restart triggered — data will resume in ~10s"
                : $"Error: {response.Message}";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var response = await _pipeClient.SendRequestAsync<GetHistoryResponse>(
                new GetHistoryRequest { MaxEvents = 200 });
            RecentEvents.Clear();
            foreach (var evt in response.Events)
                RecentEvents.Add(evt);
        }
        catch (Exception ex) { CommandResult = $"Error loading history: {ex.Message}"; }
    }

    // ===================== CONFIG =====================

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        try
        {
            var response = await _pipeClient.SendRequestAsync<GetConfigResponse>(
                new GetConfigRequest());
            var config = response.Config;
            _loadedConfig = config;

            // Beeper
            SelectedBeeperMode = config.Beeper.Mode.ToString();
            BeeperSchedule.Clear();
            foreach (var e in config.Beeper.Schedule)
                BeeperSchedule.Add(new ScheduleEntryViewModel
                {
                    Day = e.Day,
                    StartTime = e.Start.ToString("HH:mm"),
                    EndTime = e.End.ToString("HH:mm"),
                    IsMuted = e.Muted
                });

            // Shutdown
            ShutdownEnabled = config.Shutdown.Enabled;
            ShutdownBatteryThreshold = config.Shutdown.BatteryThresholdPercent;
            ShutdownRuntimeThreshold = config.Shutdown.RuntimeThresholdSeconds;
            ShutdownGracePeriod = config.Shutdown.GracePeriodSeconds;
            ShutdownUseHibernate = config.Shutdown.UseHibernate;
            ShutdownCustomCommand = config.Shutdown.CustomShutdownCommand;

            // Email
            EmailEnabled = config.Alerts.Email.Enabled;
            SmtpHost = config.Alerts.Email.SmtpHost;
            SmtpPort = config.Alerts.Email.SmtpPort;
            SmtpUseTls = config.Alerts.Email.UseTls;
            SmtpUsername = config.Alerts.Email.Username;
            SmtpPassword = config.Alerts.Email.Password;
            EmailFrom = config.Alerts.Email.FromAddress;
            EmailTo = string.Join(", ", config.Alerts.Email.ToAddresses);
            WebhookUrlsText = string.Join("\r\n", config.Alerts.WebhookUrls);

            // Alert event types
            AlertEventTypes.Clear();
            foreach (var et in Enum.GetValues<PowerEventType>())
                AlertEventTypes.Add(new EventTypeToggle
                {
                    EventType = et,
                    DisplayName = Regex.Replace(et.ToString(), "(?<!^)([A-Z])", " $1"),
                    IsEnabled = config.Alerts.EnabledEventTypes.Contains(et)
                });

            // Scripts
            ScriptTriggerList.Clear();
            foreach (var t in config.ScriptTriggers)
                ScriptTriggerList.Add(new ScriptTriggerViewModel
                {
                    EventType = t.EventType.ToString(),
                    ScriptPath = t.ScriptPath,
                    Arguments = t.Arguments,
                    TimeoutSeconds = t.TimeoutSeconds
                });

            // NUT config
            NutHost = config.Nut.Host;
            NutPort = config.Nut.Port;
            NutUpsName = config.Nut.UpsName;
            NutUsername = config.Nut.Username;
            NutPassword = config.Nut.Password;

            ConfigLoaded = true;
            ConfigStatus = "Configuration loaded";
        }
        catch (Exception ex) { ConfigStatus = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        try
        {
            var config = new AppConfig
            {
                Nut = new NutConfig
                {
                    Host = NutHost,
                    Port = NutPort,
                    UpsName = NutUpsName,
                    Username = NutUsername.Trim(),
                    Password = NutPassword,
                    NutBinPath = _loadedConfig?.Nut.NutBinPath ?? "C:/NUT/bin",
                    NutConfPath = _loadedConfig?.Nut.NutConfPath ?? "C:/NUT/etc"
                },
                PollIntervalMs = _loadedConfig?.PollIntervalMs ?? 2000,
                EventRetentionDays = _loadedConfig?.EventRetentionDays ?? 90,
                Beeper = new BeeperConfig
                {
                    Mode = Enum.Parse<BeeperMode>(SelectedBeeperMode),
                    Schedule = BeeperSchedule.Select(s => new BeeperScheduleEntry(
                        s.Day,
                        TimeOnly.Parse(s.StartTime),
                        TimeOnly.Parse(s.EndTime),
                        s.IsMuted)).ToList()
                },
                Shutdown = new ShutdownConfig
                {
                    Enabled = ShutdownEnabled,
                    BatteryThresholdPercent = ShutdownBatteryThreshold,
                    RuntimeThresholdSeconds = ShutdownRuntimeThreshold,
                    GracePeriodSeconds = ShutdownGracePeriod,
                    UseHibernate = ShutdownUseHibernate,
                    CustomShutdownCommand = ShutdownCustomCommand
                },
                Alerts = new AlertConfig
                {
                    Email = new EmailSettings
                    {
                        Enabled = EmailEnabled,
                        SmtpHost = SmtpHost,
                        SmtpPort = SmtpPort,
                        UseTls = SmtpUseTls,
                        Username = SmtpUsername,
                        Password = SmtpPassword,
                        FromAddress = EmailFrom,
                        ToAddresses = EmailTo
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList()
                    },
                    WebhookUrls = WebhookUrlsText
                        .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    EnabledEventTypes = AlertEventTypes
                        .Where(e => e.IsEnabled)
                        .Select(e => e.EventType)
                        .ToHashSet()
                },
                ScriptTriggers = ScriptTriggerList.Select(s => new ScriptTrigger(
                    Enum.Parse<PowerEventType>(s.EventType), s.ScriptPath, s.Arguments, s.TimeoutSeconds)).ToList()
            };

            var response = await _pipeClient.SendRequestAsync<CommandResponse>(
                new UpdateConfigRequest { Config = config });
            ConfigStatus = response.Success ? response.Message : $"Error: {response.Message}";
            _loadedConfig = config;
        }
        catch (Exception ex) { ConfigStatus = $"Error: {ex.Message}"; }
    }

    // ===================== NUT ADVANCED =====================

    [RelayCommand]
    private async Task LoadNutInfoAsync()
    {
        try
        {
            var response = await _pipeClient.SendRequestAsync<GetNutInfoResponse>(
                new GetNutInfoRequest());
            NutDriverRunning = response.DriverRunning;
            NutUpsdRunning = response.UpsdRunning;
            NutServiceConnected = response.Connected;
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SendNutInstantCommandAsync()
    {
        if (string.IsNullOrEmpty(SelectedCommand)) return;
        try
        {
            var response = await _pipeClient.SendRequestAsync<CommandResponse>(
                new SendNutCommandRequest { Command = SelectedCommand });
            CommandResult = response.Success
                ? $"✓ {SelectedCommand}"
                : $"Error: {response.Message}";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SetNutVariableAsync()
    {
        if (string.IsNullOrEmpty(NutVariableName)) return;
        try
        {
            var response = await _pipeClient.SendRequestAsync<CommandResponse>(
                new SetNutVariableRequest { Name = NutVariableName, Value = NutVariableValue });
            CommandResult = response.Success
                ? $"✓ Set {NutVariableName}"
                : $"Error: {response.Message}";
        }
        catch (Exception ex) { CommandResult = $"Error: {ex.Message}"; }
    }

    // ===================== LIST MANAGEMENT =====================

    [RelayCommand]
    private void AddScheduleEntry() => BeeperSchedule.Add(new ScheduleEntryViewModel());

    [RelayCommand]
    private void RemoveScheduleEntry(ScheduleEntryViewModel? entry)
    {
        if (entry is not null) BeeperSchedule.Remove(entry);
    }

    [RelayCommand]
    private void AddScriptTrigger() => ScriptTriggerList.Add(new ScriptTriggerViewModel());

    [RelayCommand]
    private void RemoveScriptTrigger(ScriptTriggerViewModel? trigger)
    {
        if (trigger is not null) ScriptTriggerList.Remove(trigger);
    }

    // ===================== NAVIGATION =====================

    public void NavigateTo(string page) => SelectedPage = page;
}
