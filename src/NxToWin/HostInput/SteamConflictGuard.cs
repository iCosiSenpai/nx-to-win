using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NxToWin.HostInput;

public static class SteamConflictGuard
{
    internal enum SteamState { Closed, Running, NoJoy, UnknownArguments }
    internal sealed record ClientStatus(SteamState Steam, string[] OtherClients);

    private static readonly (string Name, string[] Processes)[] ControllerClients =
    [
        ("DS4Windows", ["DS4Windows"]),
        ("reWASD", ["reWASD", "reWASDService", "reWASDEngine"]),
        ("BetterJoy", ["BetterJoy", "BetterJoyForCemu"]),
        ("pad2go", ["pad2go"]),
        ("Switch2NativeControllerDriver", ["Switch2Native", "Switch2Manager", "Switch2JoyCons"])
    ];

    internal static ClientStatus ReadClientStatus()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try { names.Add(process.ProcessName); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        var clients = ControllerClients.Where(client => client.Processes.Any(names.Contains)).Select(client => client.Name).ToList();
        if (names.Any(IsSwitch2ConnectProcess)) clients.Insert(0, "Switch2Connect");
        return new ClientStatus(names.Contains("steam") ? ReadSteamArguments() : SteamState.Closed, clients.ToArray());
    }

    internal static bool IsSwitch2ConnectProcess(string name) => Regex.IsMatch(name,
        @"^Switch2Connect(?:[ _-]+v?\d[\w. -]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static List<Process> FindSwitch2ConnectProcesses()
    {
        var clients = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (IsSwitch2ConnectProcess(process.ProcessName))
                {
                    clients.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return clients;
    }

    private static SteamState ReadSteamArguments()
    {
        try
        {
            using var query = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name = 'steam.exe'");
            query.Options.Timeout = TimeSpan.FromSeconds(2);
            using var instances = query.Get();
            bool found = false;
            bool unknown = false;
            foreach (ManagementBaseObject instance in instances)
            {
                using (instance)
                {
                    found = true;
                    if (instance["CommandLine"] is not string commandLine)
                        unknown = true;
                    else if (!HasNoJoyArgument(commandLine))
                        return SteamState.Running;
                }
            }
            return !found ? SteamState.Closed : unknown ? SteamState.UnknownArguments : SteamState.NoJoy;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return SteamState.UnknownArguments;
        }
    }

    internal static bool HasNoJoyArgument(string commandLine) =>
        Regex.Matches(commandLine, @"[^\s""]+|""[^""]*""").Cast<Match>().Skip(1)
            .Any(argument => string.Equals(argument.Value.Trim('"'), "-nojoy", StringComparison.OrdinalIgnoreCase));

    private static string? FindSteamExecutable(Process[] processes)
    {
        foreach (var process in processes)
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (File.Exists(path)) return path;
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        string? registered = (key?.GetValue("SteamExe") as string)?.Replace('/', '\\');
        if (File.Exists(registered)) return registered;
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe");
        return File.Exists(standard) ? standard : null;
    }

    public static async Task<string> RestartSteamWithNoJoyAsync()
    {
        var processes = Process.GetProcessesByName("steam");
        try
        {
            string? executable = FindSteamExecutable(processes);
            if (executable == null) return "Eseguibile Steam non trovato.";

            if (processes.Length != 0)
            {
                using var shutdown = Process.Start(new ProcessStartInfo(executable, "-shutdown") { UseShellExecute = false });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(timeout.Token))); }
                catch (OperationCanceledException) { return "Steam non si è chiuso entro 12 secondi."; }
            }

            using var started = Process.Start(new ProcessStartInfo(executable, "-nojoy -silent") { UseShellExecute = false });
            if (started == null) return "Avvio Steam non riuscito.";
            return "Avvio di Steam richiesto con -nojoy.";
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
