using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NxToWin.Updates;
using Velopack;

namespace NxToWin;

public partial class MainWindow
{
    private readonly GitHubUpdates.Manager _updateManager = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private CancellationTokenSource? _updateOperation;
    private bool _checkingAutomatically;
    private bool _manualRestartScheduled;
    private VelopackAsset? _availableUpdate;
    private VelopackAsset? _downloadedUpdate;

    private bool CanInstallUpdates => _updateManager.IsInstalled && _updateManager.AppId == GitHubUpdates.PackageId;

    private void InitializeUpdates()
    {
        InstalledVersionText.Text = F("Versione installata · {0}", GitHubUpdates.CurrentVersion);
        AutoUpdateCheck.IsChecked = _preferences.AutomaticUpdates;
        AutoUpdateCheck.IsEnabled = CanInstallUpdates;
        AutoUpdateDescription.Text = T(CanInstallUpdates
            ? "Scarica le nuove versioni in background e le installa alla chiusura dell’app."
            : "Per gli aggiornamenti automatici usa l’installer di NxToWin da GitHub.");
        InstallUpdateButton.Content = T(CanInstallUpdates ? "Installa e riavvia" : "Apri la release su GitHub");
        Loaded += (_, _) => ConfigureAutomaticUpdates();
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(automatic: true);
    }

    private void ConfigureAutomaticUpdates()
    {
        _updateTimer.Stop();
        if (!_preferences.AutomaticUpdates || !CanInstallUpdates)
        {
            if (_checkingAutomatically) _updateOperation?.Cancel();
            if (_downloadedUpdate is { } update)
                UpdateStatusText.Text = F("Versione {0} pronta per l’installazione.", update.Version);
            return;
        }

        _updateTimer.Start();
        if (_downloadedUpdate is { } ready)
            UpdateStatusText.Text = F("Versione {0} pronta. Sarà installata alla chiusura.", ready.Version);
        _ = CheckUpdatesAsync(automatic: true);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(automatic: false);

    private Task CheckUpdatesAsync(bool automatic)
    {
        if (automatic && (!_preferences.AutomaticUpdates || !CanInstallUpdates || _downloadedUpdate is not null))
            return Task.CompletedTask;

        return RunUpdateOperationAsync(automatic, async token =>
        {
            UpdateStatusText.Text = T("Verifica aggiornamenti in corso…");
            _availableUpdate = await GitHubUpdates.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            if (_availableUpdate is not { } update)
            {
                _downloadedUpdate = null;
                UpdateStatusText.Text = T("Stai usando la versione più recente.");
                return;
            }

            if (_downloadedUpdate?.Version != update.Version) _downloadedUpdate = null;
            UpdateStatusText.Text = F("È disponibile la versione {0}.", update.Version);
            if (automatic)
            {
                await DownloadUpdateAsync(update, token);
                UpdateStatusText.Text = F("Versione {0} pronta. Sarà installata alla chiusura.", update.Version);
            }
        });
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } update) return;
        await RunUpdateOperationAsync(automatic: false, async token =>
        {
            if (!CanInstallUpdates)
            {
                Process.Start(new ProcessStartInfo(GitHubUpdates.ReleasesPage) { UseShellExecute = true })?.Dispose();
                return;
            }

            await DownloadUpdateAsync(update, token);
            token.ThrowIfCancellationRequested();
            _updateManager.WaitExitThenApplyUpdates(update, restart: true);
            _manualRestartScheduled = true;
            Close();
        });
    }

    private async Task DownloadUpdateAsync(VelopackAsset update, CancellationToken token)
    {
        UpdateStatusText.Text = F("Download della versione {0}…", update.Version);
        await _updateManager.DownloadUpdatesAsync(new UpdateInfo(update, false), cancelToken: token);
        await _updateManager.VerifyAsync(update);
        token.ThrowIfCancellationRequested();
        _downloadedUpdate = update;
    }

    private async Task RunUpdateOperationAsync(bool automatic, Func<CancellationToken, Task> operation)
    {
        if (_closing || _updateOperation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _updateOperation = cancellation;
        _checkingAutomatically = automatic;
        CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            UpdateStatusText.Text = "";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = T("Aggiornamento non riuscito. Riprova tra poco.");
            AppendLog($"Update: {ex.Message}");
        }
        finally
        {
            _updateOperation = null;
            _checkingAutomatically = false;
            CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
            InstallUpdateButton.Visibility = Show(_availableUpdate is not null);
        }
    }

    private async Task ApplyAutomaticUpdateOnCloseAsync()
    {
        if (!_preferences.AutomaticUpdates || _manualRestartScheduled || _downloadedUpdate is not { } update) return;
        try
        {
            await _updateManager.VerifyAsync(update);
            _updateManager.WaitExitThenApplyUpdates(update, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            AppendLog($"Update: {ex.Message}");
        }
    }
}
