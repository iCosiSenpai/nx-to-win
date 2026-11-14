using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NxToWin.Dsp;
using NxToWin.Hardware;
using NxToWin.HostInput;
using NxToWin.Models;
using NxToWin.VirtualOutput;

namespace NxToWin;

public partial class MainWindow : Window
{
    private sealed record Choice<T>(T Value, string Label, string Help = "")
    {
        public override string ToString() => Label;
    }

    private static readonly Choice<EmulationMode>[] Outputs =
    [
        new(EmulationMode.ViGEm_Xbox360, "Xbox 360 · XInput", "ViGEmBus. Per giochi Windows che richiedono XInput."),
        new(EmulationMode.ViGEm_DualShock4, "DualShock 4 · ViGEm", "ViGEmBus. Pulsanti e vibrazione in formato DualShock 4."),
        new(EmulationMode.SwitchPro_NativeVHF, "Switch Pro · VHF", "WinUHid. Report Nintendo Switch e movimento."),
        new(EmulationMode.Switch2Pro_NativeUSBIP, "Switch 2 Pro · USB/IP", "Driver USB/IP di Windows. Il gioco deve supportare Switch 2 Pro USB."),
        new(EmulationMode.DualSense_PS5_VHF, "DualSense · VHF", "WinUHid. Report PlayStation 5 e movimento."),
        new(EmulationMode.XboxOne_Impulse_VHF, "Xbox One · HID VHF", "WinUHid. Profilo HID con vibrazione sui grilletti.")
    ];

    private readonly BridgeConfiguration _config = new();
    private readonly ControllerDiscoveryManager _discovery;
    private readonly CemuhookDsuServer _dsu = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly object _routeLock = new();
    private readonly object _rumbleRoute = new();
    private readonly RumbleOutput _rumbleOutput;
    private ControllerProfiles _profiles;
    private IVirtualOutputDevice? _output;
    private volatile IPhysicalController? _activeController;
    private RoutedInput _lastInput = new(new(), SwitchButtons.None);
    private bool _updatingUi = true;
    private bool _refreshingControllers;
    private bool _readingBattery;
    private volatile bool _testingRumble;
    private CancellationTokenSource? _rumblePlayback;
    private VibrationFrame _previewLeft;
    private VibrationFrame _previewRight;
    private Task? _previewTask;
    private volatile bool _closing;
    private bool _readyToClose;
    private long _nextStatusCheck;
    private long _nextClientCheck;
    private bool _checkingClients;
    private long _nextBatteryCheck;

    public MainWindow()
    {
        InitializeComponent();
        _rumbleOutput = new RumbleOutput(AppendLog);
        SourceInitialized += (_, _) => FitToWorkArea();
        try
        {
            _profiles = ControllerProfiles.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _profiles = new();
            AppendLog($"Profili: {ex.Message}");
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveProfiles();
        };
        _discovery = new ControllerDiscoveryManager(_config);
        _discovery.LogMessage += AppendLog;
        _discovery.ControllerConnected += OnControllerConnected;
        _discovery.ControllerDisconnected += OnControllerDisconnected;

        PopulateChoices();
        LoadProfileControls(_profiles.Draft);
        RefreshProfileList();
        _updatingUi = false;
        ApplySettings();

        UpdateRumbleAvailability();
        UpdateControllerSettings();

        _config.HostButtons.ActionTriggered += action => AppendLog($"Azione Windows · {action.ToDisplay()}");
        _discovery.Start();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();
        Closing += OnWindowClosing;
    }

    private void PopulateChoices()
    {
        EmulationModeCombo.ItemsSource = Outputs;
        OfflineControllerCombo.ItemsSource = new Choice<ushort>[]
        {
            new(NintendoIds.ProController2Pid, "Switch 2 Pro Controller"),
            new(NintendoIds.JoyCon2LeftPid, "Joy-Con 2 sinistro"),
            new(NintendoIds.JoyCon2RightPid, "Joy-Con 2 destro"),
            new(NintendoIds.ProController1Pid, "Switch Pro Controller"),
            new(NintendoIds.JoyCon1LeftPid, "Joy-Con sinistro"),
            new(NintendoIds.JoyCon1RightPid, "Joy-Con destro"),
            new(NintendoIds.NsoGameCubePid, "GameCube per Switch 2")
        };
        OfflineControllerCombo.SelectedIndex = 0;
        LayoutCombo.ItemsSource = new Choice<AbxyLayout>[]
        {
            new(AbxyLayout.Nintendo, "Mantieni le posizioni Nintendo"),
            new(AbxyLayout.Xbox, "Scambia A ↔ B e X ↔ Y"),
            new(AbxyLayout.Inverted, "Scambia A ↔ Y e B ↔ X")
        };

        Choice<ExtraButtonAction>[] grip =
        [
            new(ExtraButtonAction.None, "Mantieni originale"),
            new(ExtraButtonAction.MapToL3, "Stick sinistro (L3)"),
            new(ExtraButtonAction.MapToR3, "Stick destro (R3)"),
            new(ExtraButtonAction.MapToA, "Pulsante A"),
            new(ExtraButtonAction.MapToB, "Pulsante B"),
            new(ExtraButtonAction.MapToX, "Pulsante X"),
            new(ExtraButtonAction.MapToY, "Pulsante Y"),
            new(ExtraButtonAction.MapToL, "Dorsale L"),
            new(ExtraButtonAction.MapToR, "Dorsale R"),
            new(ExtraButtonAction.MapToZL, "Grilletto ZL"),
            new(ExtraButtonAction.MapToZR, "Grilletto ZR"),
            new(ExtraButtonAction.MapToDpadUp, "Croce ↑"),
            new(ExtraButtonAction.MapToDpadDown, "Croce ↓"),
            new(ExtraButtonAction.MapToDpadLeft, "Croce ←"),
            new(ExtraButtonAction.MapToDpadRight, "Croce →"),
            new(ExtraButtonAction.MapToCapture, "Cattura"),
            new(ExtraButtonAction.MapToHome, "Home"),
            new(ExtraButtonAction.RecenterGyro, "Ricentra giroscopio"),
            new(ExtraButtonAction.GyroAimHold, "Sospendi giroscopio durante la pressione"),
            new(ExtraButtonAction.MacroCombo_B_Plus_X, "B + X"),
            new(ExtraButtonAction.MacroCombo_A_Plus_B, "A + B")
        ];
        var windowsActions = Enum.GetValues<ExtraButtonAction>()
            .Select(action => (Action: action, Host: ButtonRemapper.GetWindowsAction(action)))
            .Where(item => item.Host.HasValue)
            .Select(item => new Choice<ExtraButtonAction>(item.Action, item.Host.GetValueOrDefault().ToDisplay()));
        var extraActions = grip.Concat(windowsActions).ToArray();
        foreach (var combo in new[] { GlActionCombo, GrActionCombo, SlActionCombo, SrActionCombo })
            combo.ItemsSource = extraActions;

        var actions = Enum.GetValues<HostAction>().Select(a => new Choice<HostAction>(a, a.ToDisplay())).ToArray();
        foreach (ComboBox combo in new[] { CaptureTapCombo, CaptureHoldCombo, HomeTapCombo, HomeHoldCombo, ChatCTapCombo, ChatCHoldCombo })
            combo.ItemsSource = actions;
    }

    private static T ChoiceValue<T>(ComboBox combo, T fallback) =>
        combo.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private static void Select<T>(ComboBox combo, T value)
    {
        combo.SelectedItem = combo.Items.OfType<Choice<T>>().FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value))
            ?? combo.Items.OfType<Choice<T>>().First();
    }

    private void LoadProfileControls(ControllerProfile profile)
    {
        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        Select(EmulationModeCombo, profile.Output);
        Select(LayoutCombo, profile.Layout);
        Select(GlActionCombo, profile.Gl);
        Select(GrActionCombo, profile.Gr);
        Select(SlActionCombo, profile.Sl);
        Select(SrActionCombo, profile.Sr);
        Select(CaptureTapCombo, profile.CaptureTap);
        Select(CaptureHoldCombo, profile.CaptureHold);
        Select(HomeTapCombo, profile.HomeTap);
        Select(HomeHoldCombo, profile.HomeHold);
        Select(ChatCTapCombo, profile.ChatTap);
        Select(ChatCHoldCombo, profile.ChatHold);
        HoldThreshold.Value = profile.HoldMs;
        RumbleGain.Value = profile.Rumble;
        LeftDeadzone.Value = profile.LeftDeadzone;
        RightDeadzone.Value = profile.RightDeadzone;
        SteamMultiClientCheck.IsChecked = profile.SteamGuard;
        OpticalMouseCheck.IsChecked = profile.OpticalMouse;
        OpticalSensitivity.Value = profile.OpticalMouseSensitivity;
        MagFusionCheck.IsChecked = profile.Magnetometer;
        AutoMergeCheck.IsChecked = profile.AutoMergeJoyCons;
        DsuCheck.IsChecked = profile.EnableDsu;
        _updatingUi = wasUpdating;
    }

    private ControllerProfile ReadProfileControls() => new()
    {
        Output = ChoiceValue(EmulationModeCombo, EmulationMode.ViGEm_Xbox360),
        Layout = ChoiceValue(LayoutCombo, AbxyLayout.Nintendo),
        Gl = ChoiceValue(GlActionCombo, ExtraButtonAction.MapToL3),
        Gr = ChoiceValue(GrActionCombo, ExtraButtonAction.MapToR3),
        Sl = ChoiceValue(SlActionCombo, ExtraButtonAction.MapToL),
        Sr = ChoiceValue(SrActionCombo, ExtraButtonAction.MapToR),
        CaptureTap = ChoiceValue(CaptureTapCombo, HostAction.PassThrough),
        CaptureHold = ChoiceValue(CaptureHoldCombo, HostAction.PassThrough),
        HomeTap = ChoiceValue(HomeTapCombo, HostAction.PassThrough),
        HomeHold = ChoiceValue(HomeHoldCombo, HostAction.PassThrough),
        ChatTap = ChoiceValue(ChatCTapCombo, HostAction.PassThrough),
        ChatHold = ChoiceValue(ChatCHoldCombo, HostAction.PassThrough),
        HoldMs = (int)Math.Round(HoldThreshold.Value),
        Rumble = (float)RumbleGain.Value,
        LeftDeadzone = (float)LeftDeadzone.Value,
        RightDeadzone = (float)RightDeadzone.Value,
        SteamGuard = SteamMultiClientCheck.IsChecked == true,
        OpticalMouse = OpticalMouseCheck.IsChecked == true,
        OpticalMouseSensitivity = (float)OpticalSensitivity.Value,
        Magnetometer = MagFusionCheck.IsChecked == true,
        AutoMergeJoyCons = AutoMergeCheck.IsChecked == true,
        EnableDsu = DsuCheck.IsChecked == true
    };

    private void OnConfigChanged(object sender, RoutedEventArgs e)
    {
        if (!_updatingUi) ApplySettings();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OnConfigChanged(sender, e);

    private void ApplySettings()
    {
        var settings = ReadProfileControls();
        lock (_routeLock)
        {
            _config.Mode = settings.Output;
            _config.Layout = settings.Layout;
            _config.GlAction = settings.Gl;
            _config.GrAction = settings.Gr;
            _config.SlAction = settings.Sl;
            _config.SrAction = settings.Sr;
            _config.LeftStickDeadzone = settings.LeftDeadzone;
            _config.RightStickDeadzone = settings.RightDeadzone;
            _config.RumbleStrength = settings.Rumble;
            _config.SteamMultiClientGuard = settings.SteamGuard;
            _config.EnableJoyCon2OpticalMouse = settings.OpticalMouse;
            _config.OpticalMouseSensitivity = settings.OpticalMouseSensitivity;
            _config.Enable9AxisMagFusion = settings.Magnetometer;
            if (_config.EnableCemuhookDsu != settings.EnableDsu || (settings.EnableDsu && !_dsu.IsRunning && _output == null))
            {
                _config.EnableCemuhookDsu = settings.EnableDsu;
                UpdateDsu(settings.EnableDsu);
            }
            _config.HostButtons.Configure(settings);
            if (_output?.Mode != settings.Output)
                ChangeOutput(settings.Output);
        }
        _discovery.SetAutoMerge(settings.AutoMergeJoyCons);

        _profiles.Draft = settings;
        OutputHelp.Text = (EmulationModeCombo.SelectedItem as Choice<EmulationMode>)?.Help;
        SteamMultiClientCheck.IsEnabled = settings.Output == EmulationMode.SwitchPro_NativeVHF;
        HoldThresholdValue.Text = $"{settings.HoldMs} ms";
        RumbleGainValue.Text = $"{settings.Rumble:P0}";
        OpticalSensitivity.IsEnabled = settings.OpticalMouse;
        OpticalSensitivityValue.Text = $"{settings.OpticalMouseSensitivity:F1}×";
        LeftDeadzoneValue.Text = $"{settings.LeftDeadzone:P0}";
        RightDeadzoneValue.Text = $"{settings.RightDeadzone:P0}";
        UpdateProfileStatus();
        UpdateOutputStatus();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void UpdateDsu(bool enabled)
    {
        if (!enabled)
        {
            _dsu.Stop();
            AppendLog("Server movimento DSU disattivato.");
            return;
        }
        AppendLog(_dsu.Start(_config.DsuPort)
            ? $"DSU disponibile su UDP 127.0.0.1:{_config.DsuPort}."
            : $"DSU: impossibile aprire la porta UDP {_config.DsuPort}.");
    }

    private void ChangeOutput(EmulationMode mode)
    {
        DisposeOutput();
        var output = mode switch
        {
            EmulationMode.Switch2Pro_NativeUSBIP => (IVirtualOutputDevice)new UsbipSwitch2VirtualDevice(),
            EmulationMode.SwitchPro_NativeVHF => new WinUHidSwitchProVirtualDevice(),
            EmulationMode.DualSense_PS5_VHF => new WinUHidDualSenseVirtualDevice(),
            EmulationMode.XboxOne_Impulse_VHF => new WinUHidXboxOneVirtualDevice(),
            _ => new ViGEmVirtualDevice(mode)
        };
        _output = output;
        output.LogMessage += AppendLog;
        output.HdRumbleReceived += OnRumbleReceived;
        output.PlayerLedChanged += OnPlayerLedChanged;
        try
        {
            if (output.Start())
                output.UpdateControllerState(_lastInput, _config);
        }
        catch (Exception ex)
        {
            AppendLog($"Uscita virtuale: {ex.Message}");
        }
    }

    private void DisposeOutput()
    {
        if (_output == null) return;
        lock (_rumbleRoute)
        {
            _output.HdRumbleReceived -= OnRumbleReceived;
            _rumbleOutput.ClearGame();
            if (!_closing && !_testingRumble) _ = _rumbleOutput.StopAsync(_activeController);
        }
        _output.PlayerLedChanged -= OnPlayerLedChanged;
        try { _output.Dispose(); }
        catch (Exception ex) { AppendLog($"Chiusura uscita virtuale: {ex.Message}"); }
        _output = null;
    }

    private void OnControllerConnected(IPhysicalController controller)
    {
        controller.StateUpdated += OnPhysicalState;
        _ = Dispatcher.InvokeAsync(RefreshControllers);
    }

    private void OnControllerDisconnected(IPhysicalController controller)
    {
        controller.StateUpdated -= OnPhysicalState;
        _ = Dispatcher.InvokeAsync(RefreshControllers);
    }

    private void RefreshControllers()
    {
        if (_closing) return;
        var controllers = _discovery.Controllers;
        var current = _activeController;
        var selected = current != null && controllers.Contains(current) ? current : controllers.FirstOrDefault();
        _refreshingControllers = true;
        ControllersListBox.ItemsSource = controllers;
        ControllersListBox.SelectedItem = selected;
        _refreshingControllers = false;
        EmptyDevices.Visibility = controllers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivateController(selected);
        _nextBatteryCheck = 0;
    }

    private void OnSelectedControllerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingControllers && !_updatingUi)
            ActivateController(ControllersListBox.SelectedItem as IPhysicalController);
    }

    private void ActivateController(IPhysicalController? controller)
    {
        if (_activeController == controller) return;
        _rumblePlayback?.Cancel();
        lock (_routeLock)
        {
            lock (_rumbleRoute)
            {
                _rumbleOutput.ClearGame();
                _ = _rumbleOutput.StopAsync(_activeController);
                _activeController = controller;
            }
            _config.HostButtons.Reset();
            _lastInput = new(new(), SwitchButtons.None);
            try { _output?.UpdateControllerState(_lastInput, _config); }
            catch (Exception ex) { AppendLog($"Aggiornamento uscita: {ex.Message}"); }
            if (_config.EnableCemuhookDsu) _dsu.BroadcastControllerState(null);
        }

        ControllerTitle.Text = controller?.Name ?? "Nessun controller";
        UpdateControllerSettings();
        UpdateRumbleAvailability();
        if (controller != null && _profiles.Assignments.TryGetValue(controller.Id, out string? profile))
            ActivateProfile(profile);
        UpdateProfileStatus();
    }

    private void OnPhysicalState(IPhysicalController controller, ControllerStateSnapshot state)
    {
        if (_activeController != controller) return;
        lock (_routeLock)
        {
            if (_closing || _activeController != controller) return;
            try
            {
                var buttons = ButtonRemapper.ApplyRemapping(state, _config, out bool recenter, out bool suppressGyro);
                if (recenter) controller.ImuFusion.Recenter();
                _lastInput = new(state, buttons, suppressGyro);
                _output?.UpdateControllerState(_lastInput, _config);
                if (_config.EnableCemuhookDsu) _dsu.BroadcastControllerState(_lastInput);
            }
            catch (Exception ex)
            {
                AppendLog($"Invio report: {ex.Message}");
                DisposeOutput();
            }
        }
    }

    private void OnRumbleReceived(StereoRumble rumble)
    {
        lock (_rumbleRoute)
        {
            if (!_closing && !_testingRumble && _activeController is { IsConnected: true, SupportsRumble: true } controller)
                _rumbleOutput.QueueGame(controller, rumble);
        }
    }

    private void OnPlayerLedChanged(int player)
    {
        if (_activeController is { IsConnected: true } controller)
            _ = controller.SetPlayerLedsAsync(player);
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: IPhysicalController controller } button)
        {
            button.IsEnabled = false;
            if (controller == _activeController)
            {
                _rumblePlayback?.Cancel();
                ActivateController(null);
            }
            await _rumbleOutput.StopAsync(controller);
            _discovery.Disconnect(controller);
            RefreshControllers();
        }
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        try { await _discovery.ScanPairedBleAndHidAsync(); }
        finally { ScanButton.IsEnabled = true; }
    }

    private void OnCenterSticksClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IPhysicalController controller }) return;
        controller.CenterSticks();
        AppendLog($"Centro stick aggiornato · {controller.Name}");
    }

    private void OnCalibrateGyroClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IPhysicalController controller }) return;
        controller.TriggerGyroCalibration();
        AppendLog($"Calibrazione giroscopio · tieni fermo {controller.Name}.");
    }

    private async void OnPingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IPhysicalController controller })
            await PlayEffectAsync("Impulso", HapticEffects.Build(
                controller.ProductId == NintendoIds.NsoGameCubePid ? "gamecube" : "pulse"), controller);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        if (now >= _nextStatusCheck)
        {
            _nextStatusCheck = now + 1500;
            UpdateOutputStatus();
        }
        if (now >= _nextClientCheck)
            _ = RefreshInputClientsAsync();

        if (WorkspaceTabs.SelectedItem == BatteryTab && now >= _nextBatteryCheck)
            _ = RefreshBatteriesAsync();

        var controller = _activeController;
        if (WorkspaceTabs.SelectedItem == ControllerTab)
            UpdateControllerMonitor(controller);

        var output = _output;
        if (WorkspaceTabs.SelectedItem == RumbleTab)
        {
            var rumble = _rumbleOutput.GetLiveRumble(controller);
            var left = _testingRumble ? _previewLeft : rumble.Left.Peak.WithStrength(_config.RumbleStrength);
            var right = _testingRumble ? _previewRight : rumble.Right.Peak.WithStrength(_config.RumbleStrength);
            LeftLraText.Text = FormatMotor("Sinistra", left);
            RightLraText.Text = FormatMotor("Destra", right);
            LeftLraBar.Value = left.Amp * 100;
            RightLraBar.Value = right.Amp * 100;
        }
        if (WorkspaceTabs.SelectedItem == DiagnosticsTab)
        {
            string dsu = _dsu.IsRunning ? $"DSU UDP {_dsu.Port}" : "DSU inattivo";
            VhfCountersText.Text = $"{output?.InputReportsSent ?? 0:N0} report inviati · {output?.RumblePacketsReceived ?? 0:N0} pacchetti vibrazione · {dsu}";
        }
    }

    private static string FormatMotor(string side, VibrationFrame frame) =>
        frame.IsSilent ? $"{side} · —" : $"{side} · {frame.FreqHz:F0} Hz · {frame.Amp:P0}";

    private void UpdateControllerMonitor(IPhysicalController? controller)
    {
        var state = controller?.LatestState;
        ControllerSubtitle.Text = state == null ? "Il monitor si aggiorna con il controller selezionato."
            : $"{controller!.TransportName}    ·    {state.ReportRateHz:F0} Hz    ·    Batteria {state.Battery.Level}";
        Canvas.SetLeft(LeftStickDot, 56 + (state?.LeftStickX ?? 0) * 53);
        Canvas.SetTop(LeftStickDot, 56 - (state?.LeftStickY ?? 0) * 53);
        Canvas.SetLeft(RightStickDot, 56 + (state?.RightStickX ?? 0) * 53);
        Canvas.SetTop(RightStickDot, 56 - (state?.RightStickY ?? 0) * 53);
        LeftStickLabel.Text = state == null ? "X —    Y —" : $"X {state.LeftStickX:F2}    Y {state.LeftStickY:F2}    ZL {state.LeftTriggerAnalog}";
        RightStickLabel.Text = state == null ? "X —    Y —" : $"X {state.RightStickX:F2}    Y {state.RightStickY:F2}    ZR {state.RightTriggerAnalog}";
        ActiveButtonsText.Text = state?.Buttons is null or SwitchButtons.None ? "—" : state.Buttons.ToString();
        AttitudeText.Text = state == null ? "—" : $"Pitch {state.PitchDeg:F1}°    Roll {state.RollDeg:F1}°    Yaw {state.YawDeg:F1}°";
        MotionText.Text = state == null ? "—" : $"Gyro {state.GyroDps_X:F1} / {state.GyroDps_Y:F1} / {state.GyroDps_Z:F1} °/s    ·    Accelerazione {state.AccelG_X:F2} / {state.AccelG_Y:F2} / {state.AccelG_Z:F2} g";
        AuxSensorsText.Text = controller?.IsSwitch2 == true && state != null
            ? $"Magnetometro {state.MagX} / {state.MagY} / {state.MagZ}" +
                (controller.IsLeftJoyCon || controller.IsRightJoyCon || controller is MergedJoyConPairController
                    ? $"    ·    Sensore ottico X {state.MouseRawX} / Y {state.MouseRawY}" : "")
            : "";

    }

    private async Task RefreshInputClientsAsync()
    {
        if (_checkingClients || _closing) return;
        _checkingClients = true;
        _nextClientCheck = Environment.TickCount64 + 4000;
        try
        {
            var status = await Task.Run(SteamConflictGuard.ReadClientStatus);
            if (_closing) return;
            var (steam, tone) = status.Steam switch
            {
                SteamConflictGuard.SteamState.Closed => ("Steam · chiuso", "Muted"),
                SteamConflictGuard.SteamState.NoJoy => ("Steam · avviato con -nojoy", "Good"),
                SteamConflictGuard.SteamState.Running => ("Steam · attivo, possibile conflitto", "Accent"),
                _ => ("Steam · attivo, modalità non rilevata", "Muted")
            };
            string others = status.OtherClients.Length == 0 ? "Altri client noti · nessuno attivo"
                : $"Possibili conflitti · {string.Join(", ", status.OtherClients)}";
            HomeSteamStatus.Text = SteamStatusText.Text = steam;
            HomeSteamStatus.Foreground = SteamStatusText.Foreground = (Brush)FindResource(tone);
            HomeClientsStatus.Text = OtherClientsStatusText.Text = others;
        }
        finally { _checkingClients = false; }
    }

    private void UpdateOutputStatus()
    {
        string label = Outputs.First(x => x.Value == _config.Mode).Label;
        string status;
        string color;
        RestartUsbipButton.IsEnabled = !_reconnectingUsbip && _config.Mode == EmulationMode.Switch2Pro_NativeUSBIP;
        if (_output is UsbipSwitch2VirtualDevice usb && usb.AttachError is string error)
            (status, color) = (error, "Danger");
        else if (_output is not { IsRunning: true })
            (status, color) = ("Uscita non disponibile", "Danger");
        else if (_output is UsbipSwitch2VirtualDevice native)
            (status, color) = native.LastHostInitialization is { } initialized
                ? ($"Ultima richiesta del protocollo alle {initialized.ToLocalTime():HH:mm:ss}", "Good")
                : ("In attesa di una richiesta del protocollo Switch 2", "Accent");
        else
            (status, color) = ("Uscita attiva", "Good");
        VhfStatusText.Text = $"{label}    ·    {status}";
        VhfStatusDot.Fill = (Brush)FindResource(color);
    }

    private void RefreshProfileList()
    {
        bool previous = _updatingUi;
        _updatingUi = true;
        ProfileCombo.ItemsSource = _profiles.Named.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        ProfileCombo.SelectedItem = _profiles.ActiveName;
        ProfileCombo.IsEnabled = _profiles.Named.Count > 0;
        DeleteProfileButton.IsEnabled = _profiles.ActiveName != null;
        ProfileName.Text = _profiles.ActiveName ?? "";
        _updatingUi = previous;
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingUi && ProfileCombo.SelectedItem is string name)
            ActivateProfile(name);
    }

    private void ActivateProfile(string name)
    {
        if (!_profiles.Named.TryGetValue(name, out var profile)) return;
        _profiles.ActiveName = name;
        RefreshProfileList();
        LoadProfileControls(profile);
        ApplySettings();
        ProfileMessage.Text = $"Profilo «{name}» caricato.";
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        string name = ProfileName.Text.Trim();
        if (name.Length == 0)
        {
            ProfileMessage.Text = "Inserisci un nome.";
            ProfileName.Focus();
            return;
        }

        string? existing = _profiles.Named.Keys.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        name = existing ?? name;
        _profiles.Named[name] = _profiles.Draft;
        _profiles.ActiveName = name;
        RefreshProfileList();
        UpdateProfileStatus();
        if (SaveProfiles()) ProfileMessage.Text = $"Profilo «{name}» salvato.";
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not string name || !_profiles.Named.Remove(name)) return;
        foreach (string key in _profiles.Assignments.Where(x => x.Value == name).Select(x => x.Key).ToArray())
            _profiles.Assignments.Remove(key);
        _profiles.ActiveName = null;
        RefreshProfileList();
        UpdateProfileStatus();
        if (SaveProfiles()) ProfileMessage.Text = $"Profilo «{name}» eliminato.";
    }

    private void OnResetProfile(object sender, RoutedEventArgs e)
    {
        _profiles.ActiveName = null;
        RefreshProfileList();
        LoadProfileControls(new());
        ApplySettings();
        ProfileMessage.Text = "Impostazioni predefinite caricate.";
    }

    private void OnAssignProfile(object sender, RoutedEventArgs e)
    {
        if (_activeController is not { } controller || _profiles.ActiveName is not string name) return;
        _profiles.Assignments[controller.Id] = name;
        UpdateProfileStatus();
        if (SaveProfiles()) ProfileMessage.Text = $"Profilo «{name}» associato.";
    }

    private void OnUnassignProfile(object sender, RoutedEventArgs e)
    {
        if (_activeController is not { } controller) return;
        _profiles.Assignments.Remove(controller.Id);
        UpdateProfileStatus();
        if (SaveProfiles()) ProfileMessage.Text = "Associazione rimossa.";
    }

    private void UpdateProfileStatus()
    {
        string? active = _profiles.ActiveName;
        bool modified = active != null && _profiles.Named.TryGetValue(active, out var saved) && saved != _profiles.Draft;
        ActiveProfileText.Text = active == null ? "Impostazioni personali" : $"Profilo · {active}{(modified ? " *" : "")}";
        var controller = _activeController;
        string? assigned = null;
        if (controller != null) _profiles.Assignments.TryGetValue(controller.Id, out assigned);
        ProfileAssignmentText.Text = controller == null ? "Seleziona un controller nella scheda Controller."
            : $"{controller.Name} · {assigned ?? "Nessun profilo associato"}";
        AssignProfileButton.IsEnabled = controller != null && active != null && assigned != active;
        UnassignProfileButton.IsEnabled = assigned != null;
    }

    private bool SaveProfiles()
    {
        try
        {
            _profiles.Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProfileMessage.Text = "Impossibile salvare i profili.";
            AppendLog($"Salvataggio profili: {ex.Message}");
            return false;
        }
    }

    private async void OnRefreshBatteries(object sender, RoutedEventArgs e) => await RefreshBatteriesAsync();

    private async Task RefreshBatteriesAsync()
    {
        if (_readingBattery || _closing) return;
        _nextBatteryCheck = Environment.TickCount64 + 10000;
        _readingBattery = true;
        RefreshBatteryButton.IsEnabled = false;
        try
        {
            var nintendo = _discovery.Controllers.SelectMany(c => c is MergedJoyConPairController pair
                ? new[] { pair.LeftJoyCon, pair.RightJoyCon }
                : new[] { c }).Select(c => new BatteryEntry(c.Name, c.LatestState.Battery)).ToArray();
            var others = await Task.Run(BatteryDiagnostics.Read);
            BatteryItems.ItemsSource = nintendo.Concat(others).ToArray();
            BatteryStatus.Text = nintendo.Length + others.Count == 0 ? "Nessun dispositivo rilevato."
                : $"Ultima lettura alle {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            BatteryStatus.Text = "Lettura batterie non disponibile.";
            AppendLog($"Batterie: {ex.Message}");
        }
        finally
        {
            RefreshBatteryButton.IsEnabled = true;
            _readingBattery = false;
        }
    }

    private async void OnSteamNoJoyGuardClick(object sender, RoutedEventArgs e)
    {
        SteamGuardBtn.IsEnabled = false;
        try { AppendLog(await SteamConflictGuard.RestartSteamWithNoJoyAsync()); }
        catch (Exception ex) { AppendLog($"Riavvio Steam: {ex.Message}"); }
        finally
        {
            SteamGuardBtn.IsEnabled = true;
            _nextClientCheck = 0;
        }
    }

    private async void OnTakeOverClick(object sender, RoutedEventArgs e)
    {
        TakeOverButton.IsEnabled = false;
        try
        {
            var processes = SteamConflictGuard.FindSwitch2ConnectProcesses();
            try
            {
                foreach (var process in processes)
                {
                    if (!process.HasExited)
                    {
                        if (process.CloseMainWindow())
                        {
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            try { await process.WaitForExitAsync(timeout.Token); }
                            catch (OperationCanceledException) { }
                        }
                        if (!process.HasExited)
                        {
                            process.Kill();
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                        }
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            AppendLog(processes.Count == 0 ? "Switch2Connect non in esecuzione. Ricerca controller." : "Switch2Connect chiuso. Ricerca controller.");
            await _discovery.ScanPairedBleAndHidAsync();
        }
        catch (Exception ex) { AppendLog($"Switch2Connect: {ex.Message}"); }
        finally
        {
            TakeOverButton.IsEnabled = true;
            _nextClientCheck = 0;
        }
    }

    private async void OnCheckSdlClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Libreria SDL 3|SDL3.dll", FileName = "SDL3.dll", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length is <= 0 or > 32 * 1024 * 1024)
                throw new IOException("Dimensione SDL3.dll non valida.");
            byte[] bytes = await File.ReadAllBytesAsync(dialog.FileName);
            SdlCheckText.Text = SdlSwitch2Compatibility.HasDriver(bytes)
                ? "Driver Switch 2 con libusb rilevato in questa SDL3.dll."
                : "Questa SDL3.dll non espone il driver Switch 2 con libusb. Usa una build SDL compatibile oppure Switch Pro · VHF.";
            AppendLog($"Verifica SDL3.dll · {SdlCheckText.Text}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SdlCheckText.Text = $"Verifica SDL3.dll non riuscita. {ex.Message}";
        }
    }

    private bool _reconnectingUsbip;

    private async void OnRestartUsbipClick(object sender, RoutedEventArgs e)
    {
        if (_reconnectingUsbip || _config.Mode != EmulationMode.Switch2Pro_NativeUSBIP) return;
        _reconnectingUsbip = true;
        RestartUsbipButton.IsEnabled = false;
        try
        {
            lock (_routeLock) DisposeOutput();
            await Task.Delay(350);
            lock (_routeLock)
                if (!_closing && _config.Mode == EmulationMode.Switch2Pro_NativeUSBIP && _output == null)
                    ChangeOutput(_config.Mode);
        }
        finally
        {
            _reconnectingUsbip = false;
            if (!_closing) UpdateOutputStatus();
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedItem = SettingsTab;

    private void OnOfflineControllerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingUi) UpdateControllerSettings();
    }

    private void UpdateControllerSettings()
    {
        var controller = _activeController;
        ushort model = controller?.ProductId ?? ChoiceValue(OfflineControllerCombo, NintendoIds.ProController2Pid);
        var buttons = controller switch
        {
            MergedJoyConPairController pair => NintendoIds.ConfigurableButtons(pair.LeftJoyCon.ProductId)
                                             | NintendoIds.ConfigurableButtons(pair.RightJoyCon.ProductId),
            _ => NintendoIds.ConfigurableButtons(model)
        };
        GripMappings.Visibility = Show(buttons.HasFlag(SwitchButtons.GL));
        RailMappings.Visibility = Show((buttons & (SwitchButtons.SL_L | SwitchButtons.SL_R)) != 0);
        LeftStickSettings.Visibility = Show(!NintendoIds.IsRightJoyCon(model));
        RightStickSettings.Visibility = Show(!NintendoIds.IsLeftJoyCon(model));
        CaptureShortcuts.Visibility = Show(buttons.HasFlag(SwitchButtons.Capture));
        HomeShortcuts.Visibility = Show(buttons.HasFlag(SwitchButtons.Home));
        ChatCShortcuts.Visibility = Show(buttons.HasFlag(SwitchButtons.ChatC));
        ShortcutTargetText.Text = controller?.Name ?? "Modello per la configurazione offline";
        OfflineControllerCombo.Visibility = Show(controller == null);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void FitToWorkArea()
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(source.Handle, 2), ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double width = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
        double height = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = Math.Min(Width, width);
        Height = Math.Min(Height, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private async void OnHapticEffectClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string effect, Content: string title })
            await PlayEffectAsync(title, HapticEffects.Build(effect));
    }

    private async void OnOcarinaTrackClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string track, Content: string title })
            await PlayEffectAsync(title, OcarinaHaptics.Build(track));
    }

    private void OnStopRumbleClick(object sender, RoutedEventArgs e) => _rumblePlayback?.Cancel();

    private void UpdateRumbleAvailability()
    {
        var controller = _activeController;
        bool connected = controller is { IsConnected: true, SupportsRumble: true };
        bool gameCube = controller?.ProductId == NintendoIds.NsoGameCubePid;
        RumbleTests.Visibility = Show(!gameCube);
        RumbleTests.IsEnabled = connected && !_testingRumble;
        GameCubeTest.Visibility = Show(gameCube);
        GameCubeTest.IsEnabled = connected && !_testingRumble;
        StereoEffects.Visibility = Show(controller?.IsLeftJoyCon != true && controller?.IsRightJoyCon != true);
        LeftMotorTest.Visibility = Show(controller?.IsRightJoyCon != true);
        RightMotorTest.Visibility = Show(controller?.IsLeftJoyCon != true);
        OcarinaTests.IsEnabled = RumbleTests.IsEnabled && !gameCube;
        OcarinaDescription.Text = gameCube ? "Il motore GameCube supporta il test a impulso."
            : "Melodie con attacco e rilascio sui motori HD Rumble.";
        RumbleTargetText.Text = connected ? controller!.Name : controller == null
            ? "Collega un controller per provare i motori."
            : "L’uscita di vibrazione del controller selezionato non è disponibile.";
        StopRumbleButton.IsEnabled = _testingRumble;
    }

    private async Task PlayEffectAsync(string label, (VibrationFrame Left, VibrationFrame Right, int Milliseconds)[] steps,
        IPhysicalController? target = null)
    {
        if (_closing || _testingRumble || (target ?? _activeController) is not { IsConnected: true, SupportsRumble: true } controller ||
            steps.Length == 0) return;

        using var playback = new CancellationTokenSource();
        _rumblePlayback = playback;
        var active = _activeController;
        lock (_rumbleRoute)
        {
            _testingRumble = true;
            _rumbleOutput.ClearGame();
        }
        _previewLeft = _previewRight = VibrationFrame.Silence;
        UpdateRumbleAvailability();
        RumblePlaybackText.Text = $"In riproduzione · {label}";
        var task = PlayPatternAsync(label, steps, controller, active, playback.Token);
        _previewTask = task;
        await task;
        _previewTask = null;
        _rumblePlayback = null;
        lock (_rumbleRoute) _testingRumble = false;
        if (!_closing) UpdateRumbleAvailability();
    }

    private async Task PlayPatternAsync(string label, (VibrationFrame Left, VibrationFrame Right, int Milliseconds)[] steps,
        IPhysicalController controller, IPhysicalController? active, CancellationToken token)
    {
        string result = $"Completato · {label}";
        try
        {
            if (active != controller) await _rumbleOutput.StopAsync(active);
            AppendLog($"Test vibrazione · {label} · {controller.Name}");

            const int sampleMs = 5;
            const int packetMs = 3 * sampleMs;
            int duration = steps.Sum(step => step.Milliseconds);
            int step = 0;
            int end = steps[0].Milliseconds;
            StereoRumble Sample(int time)
            {
                if (time >= duration) return StereoRumble.Silence;
                while (time >= end)
                    end += steps[++step].Milliseconds;
                return new StereoRumble(steps[step].Left, steps[step].Right);
            }

            var clock = Stopwatch.StartNew();
            int position = 0;
            while (position < duration)
            {
                token.ThrowIfCancellationRequested();
                if (!controller.IsConnected)
                {
                    result = "Test interrotto. Controller disconnesso.";
                    return;
                }
                var a = Sample(position);
                var b = Sample(position + sampleMs);
                var c = Sample(position + 2 * sampleMs);
                var packet = new StereoRumble(
                    new MotorSamples(a.Left.First, b.Left.First, c.Left.First, 3),
                    new MotorSamples(a.Right.First, b.Right.First, c.Right.First, 3));
                _previewLeft = packet.Left.Peak.WithStrength(_config.RumbleStrength);
                _previewRight = packet.Right.Peak.WithStrength(_config.RumbleStrength);
                await _rumbleOutput.SendAsync(controller, packet);

                position += packetMs;
                double remaining = position - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0)
                    await Task.Delay((int)Math.Ceiling(remaining), token);
                else
                    position = Math.Max(position, (int)(clock.Elapsed.TotalMilliseconds / packetMs) * packetMs);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = $"Interrotto · {label}";
        }
        catch (Exception ex)
        {
            result = $"Test non riuscito · {label}";
            AppendLog($"Test vibrazione: {ex.Message}");
        }
        finally
        {
            await _rumbleOutput.StopAsync(controller);
            if (!_closing)
            {
                RumblePlaybackText.Text = result;
                AppendLog($"Test vibrazione · {result}");
            }
        }
    }
    private static readonly object LogFileLock = new();

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        try
        {
            lock (LogFileLock)
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "nxtowin_live.log"), line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!_closing)
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (LogTextBox.Text.Length > 32000) LogTextBox.Text = LogTextBox.Text[^24000..];
                LogTextBox.AppendText(line);
                LogTextBox.ScrollToEnd();
            });
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogTextBox.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_readyToClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _uiTimer.Stop();
        _saveTimer.Stop();
        _rumblePlayback?.Cancel();
        _rumbleOutput.ClearGame();
        if (_previewTask is { } preview) await preview;
        await _rumbleOutput.StopAsync(_activeController);
        SaveProfiles();
        _discovery.Dispose();
        lock (_routeLock)
        {
            DisposeOutput();
            _dsu.Dispose();
        }
        _readyToClose = true;
        _ = Dispatcher.InvokeAsync(Close);
    }
}
