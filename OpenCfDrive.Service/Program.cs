using System.ComponentModel;
using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Service;

ConsoleOutput.Connect();

var roots = DriveSettingsFile.Current().Roots;

// --stop: ends the other running copies of this same exe (the installer runs
// it before replacing or removing the files). Copies elsewhere are left alone.
if (args.Contains("--stop", StringComparer.OrdinalIgnoreCase))
{
    var stopped = StopOtherInstances();
    Console.WriteLine($"Stopped {stopped} running instance(s).");
    if (!args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
    {
        return 0;
    }
}

if (args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
{
    var registrar = new SyncRootRegistrar();
    var code = 0;
    foreach (var root in roots)
    {
        var result = registrar.Unregister(root.SyncRoot);
        Console.WriteLine(result.Message);
        code |= result.Success ? 0 : 1;
    }

    return code;
}

// One provider per drive: a second start (the Start Menu shortcut clicked
// twice) would connect to the same sync root and upload everything twice.
// Owned, not just created: the app opens the mutex for a moment to see whether
// the drive's service runs, so its existence alone proves nothing. The wait
// covers such a moment. One mutex per drive, so the app can ask about each.
var instances = new List<Mutex>();
foreach (var root in roots)
{
    var instance = new Mutex(initiallyOwned: false, ServiceControl.InstanceMutexName(root.SyncRoot));
    bool firstInstance;
    try
    {
        firstInstance = instance.WaitOne(TimeSpan.FromSeconds(2));
    }
    catch (AbandonedMutexException)
    {
        firstInstance = true; // the previous copy ended without releasing it
    }

    if (!firstInstance)
    {
        Console.WriteLine($"OpenCfDrive is already running for '{root.SyncRoot}'.");
        foreach (var held in instances)
        {
            held.ReleaseMutex();
            held.Dispose();
        }

        return 0;
    }

    instances.Add(instance);
}

var builder = Host.CreateApplicationBuilder(args);
var activity = new ActivityLog();
builder.Logging.AddProvider(activity);
builder.Services.AddSingleton(activity);
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
host.Run();
return 0;

static int StopOtherInstances()
{
    var self = Environment.ProcessPath;
    var stopped = 0;
    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(self)))
    {
        using (process)
        {
            try
            {
                if (process.Id == Environment.ProcessId ||
                    !string.Equals(process.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill();
                process.WaitForExit(10_000);
                stopped++;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Another user's copy, or already gone.
            }
        }
    }

    return stopped;
}
