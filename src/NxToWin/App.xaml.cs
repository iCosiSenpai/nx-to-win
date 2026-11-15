using System.Globalization;
using System.Windows;
using NxToWin.Models;
using Velopack;

namespace NxToWin;

internal static class Bootstrap
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

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
