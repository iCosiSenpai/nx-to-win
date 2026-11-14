using System.Globalization;
using System.Windows;
using NxToWin.Models;

namespace NxToWin;

public partial class App : Application
{
    internal AppSettings Preferences { get; } = AppSettings.Load();

    protected override void OnStartup(StartupEventArgs e)
    {
        var culture = CultureInfo.GetCultureInfo(Preferences.Language == "en" ? "en-US" : "it-IT");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        base.OnStartup(e);
    }
}
