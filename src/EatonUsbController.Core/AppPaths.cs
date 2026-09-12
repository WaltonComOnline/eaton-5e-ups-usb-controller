namespace EatonUsbController.Core;

/// <summary>
/// Resolves machine-wide application and NUT paths.
/// </summary>
public static class AppPaths
{
    /// <summary>Directory for the SQLite database and other writable data.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EatonUsbController", "data");

    /// <summary>NUT sbin directory (usbhid-ups.exe, upsd.exe).</summary>
    public const string NutSbin = "C:/NUT/sbin";

    /// <summary>NUT etc directory (ups.conf, upsd.conf, upsd.users).</summary>
    public const string NutEtc = "C:/NUT/etc";
}
