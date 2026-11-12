using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>What the window asks of the app (<see cref="TrayApp"/>): its state, and the things its buttons do.</summary>
internal interface IDriveApp
{
    /// <summary>What the app read last, or null before its first read.</summary>
    DriveSnapshot? Latest { get; }

    /// <summary>A start, stop or save is under way.</summary>
    bool Busy { get; }

    Version CurrentVersion { get; }

    UpdateStatus UpdateStatus { get; }

    Task CheckForUpdatesAsync();

    Task InstallUpdateAsync(bool ask = false);

    Task StartAsync();

    Task StopAsync();

    Task RestartAsync();

    /// <summary>Saves and carries out new settings, one change per drive; returns why it could not, or null.</summary>
    Task<string?> ApplySettingsAsync(DriveSettings settings, IReadOnlyList<DriveChange> changes);

    /// <summary>Downloads rclone from rclone.org; returns why it could not, or null.</summary>
    Task<string?> DownloadRcloneAsync(IProgress<string> progress);

    void OpenFolder();

    void OpenLog();

    void OpenRcloneLog();

    /// <summary>Opens a web page, or a file or folder, as Explorer would.</summary>
    void Open(string target);
}
