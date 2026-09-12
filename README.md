# Eaton 5E Controller

Windows monitoring and control for Eaton 5E USB UPS devices. The application
combines a WPF tray interface, a Windows Service, and Network UPS Tools (NUT).

[Download the latest MSI](https://github.com/WaltonComOnline/eaton-5e-ups-usb-controller/releases/latest)

## Features

- Live battery, runtime, load, voltage, device, and power-state monitoring
- Persistent, scheduled, and on-battery beeper control
- Automatic shutdown or hibernation at configurable thresholds
- Event history, email alerts, webhooks, and event-triggered scripts
- Battery tests, writable NUT variables, and advanced UPS commands
- Automatic NUT process recovery and USB reconnect handling

## Requirements

- Windows 10 or Windows 11 x64
- Eaton 5E UPS connected by USB
- Administrator access for installation and the WinUSB driver

## Install

1. Download `Eaton5EController.msi` from the latest release.
2. Connect the UPS by USB and run the installer as administrator.
3. Open **NUT / Advanced**, enter a local NUT username and password, and select
   **Save Credentials**.

The installer places the application in
`C:\Program Files\EatonUsbController`, registers the automatic Windows Service,
and installs NUT. NUT listens only on `127.0.0.1:3493` by default. No shared
default password is included.

The installation root is organized into four folders:

- `app`: desktop application and WPF runtime
- `service`: Windows Service, configuration, and .NET runtime
- `nut`: NUT executables, configuration, and upstream licenses
- `tools`: setup and WinUSB driver support

If the dashboard reports **Service unavailable**, verify that the
`EatonUsbController` service is running and that the USB cable is connected.

## Components

- `EatonUsbController.App`: WPF tray application and dashboard
- `EatonUsbController.Service`: monitoring, alerts, shutdown, scripts, and NUT lifecycle
- `EatonUsbController.Core`: shared models, NUT client, and named-pipe protocol
- `EatonUsbController.Tests`: focused parser, protocol, and configuration tests

The app communicates with the Service through the local
`EatonUsbController` named pipe. The Service owns the `usbhid-ups` and `upsd`
processes and restarts them when required.

## Build

Install the .NET 10 SDK, WiX 6.0.2 with the UI and Util extensions, and 7-Zip.

```powershell
dotnet restore EatonUsbController.slnx
dotnet build EatonUsbController.slnx -c Release --no-restore
dotnet run --project tests\EatonUsbController.Tests -c Release --no-build
installer\build.cmd
```

The installer build downloads the pinned NUT 2.8.5 Windows archive, verifies
its SHA-256 checksum, and creates `installer\Eaton5EController.msi`.

## Security

- NUT is bound to localhost by default.
- NUT credentials are configured after installation and stored locally.
- Email credentials and webhook URLs are stored in the Service configuration.
- Event scripts run as the Windows Service account; use trusted scripts only.

## License

Bundled NUT binaries retain their upstream license and source information in the
installed `nut` directory. See the repository and release artifacts for details.
