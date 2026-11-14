using System;
using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using NxToWin.Dsp;
using NxToWin.Models;

namespace NxToWin.VirtualOutput;

/// <summary>
/// ViGEmBus Xbox 360 and DualShock 4 output with rumble forwarding.
/// </summary>
public sealed class ViGEmVirtualDevice : IVirtualOutputDevice
{
    private static ViGEmClient? s_vigemClient;
    private static readonly object s_clientLock = new();

    private readonly EmulationMode _mode;
    private IXbox360Controller? _x360;
    private IDualShock4Controller? _ds4;

    private bool _isRunning;
    private long _reportsSent;
    private long _rumbleReceived;
    private VibrationFrame _lastLeft;
    private VibrationFrame _lastRight;

    public string Name => _mode == EmulationMode.ViGEm_Xbox360
        ? "ViGEmBus Xbox 360 (XInput)"
        : "ViGEmBus DualShock 4";

    public EmulationMode Mode => _mode;
    public bool IsRunning => _isRunning;
    public long InputReportsSent => _reportsSent;
    public long RumblePacketsReceived => Interlocked.Read(ref _rumbleReceived);
    public VibrationFrame LastLeftRumble => _lastLeft;
    public VibrationFrame LastRightRumble => _lastRight;

    public event Action<StereoRumble>? HdRumbleReceived;
    public event Action<int>? PlayerLedChanged;
    public event Action<string>? LogMessage;

    public ViGEmVirtualDevice(EmulationMode mode)
    {
        if (mode != EmulationMode.ViGEm_Xbox360 && mode != EmulationMode.ViGEm_DualShock4)
        {
            throw new ArgumentException($"Mode {mode} is not a ViGEm target");
        }
        _mode = mode;
    }

    private static ViGEmClient GetOrCreateClient()
    {
        lock (s_clientLock)
        {
            s_vigemClient ??= new ViGEmClient();
            return s_vigemClient;
        }
    }

    public bool Start()
    {
        try
        {
            var client = GetOrCreateClient();
            if (_mode == EmulationMode.ViGEm_Xbox360)
            {
                _x360 = client.CreateXbox360Controller();
                _x360.AutoSubmitReport = false;
                _x360.FeedbackReceived += OnX360FeedbackReceived;
                _x360.Connect();
                _isRunning = true;
                LogMessage?.Invoke("ViGEmBus: Xbox 360 XInput connesso.");
                return true;
            }
            else
            {
                _ds4 = client.CreateDualShock4Controller();
                _ds4.AutoSubmitReport = false;
#pragma warning disable CS0618
                _ds4.FeedbackReceived += OnDs4FeedbackReceived;
#pragma warning restore CS0618
                _ds4.Connect();
                _isRunning = true;
                LogMessage?.Invoke("ViGEmBus: DualShock 4 Virtual Controller creato e connesso con successo.");
                return true;
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[ERRORE] Impossibile avviare ViGEmBus: {ex.Message}. Verifica che il servizio ViGEmBus sia attivo.");
            _isRunning = false;
            return false;
        }
    }

    public void UpdateControllerState(RoutedInput input, BridgeConfiguration config)
    {
        if (!_isRunning) return;

        if (_mode == EmulationMode.ViGEm_Xbox360 && _x360 != null)
        {
            UpdateXbox360(input.State, input.Buttons);
        }
        else if (_mode == EmulationMode.ViGEm_DualShock4 && _ds4 != null)
        {
            UpdateDualShock4(input.State, input.Buttons);
        }

        _reportsSent++;
    }

    private void UpdateXbox360(ControllerStateSnapshot state, SwitchButtons b)
    {
        if (_x360 == null) return;

        // Digital Buttons
        _x360.SetButtonState(Xbox360Button.A, b.HasFlag(SwitchButtons.B));
        _x360.SetButtonState(Xbox360Button.B, b.HasFlag(SwitchButtons.A));
        _x360.SetButtonState(Xbox360Button.X, b.HasFlag(SwitchButtons.Y));
        _x360.SetButtonState(Xbox360Button.Y, b.HasFlag(SwitchButtons.X));

        _x360.SetButtonState(Xbox360Button.LeftShoulder, b.HasFlag(SwitchButtons.L));
        _x360.SetButtonState(Xbox360Button.RightShoulder, b.HasFlag(SwitchButtons.R));
        _x360.SetButtonState(Xbox360Button.LeftThumb, b.HasFlag(SwitchButtons.LStick));
        _x360.SetButtonState(Xbox360Button.RightThumb, b.HasFlag(SwitchButtons.RStick));

        _x360.SetButtonState(Xbox360Button.Start, b.HasFlag(SwitchButtons.Plus));
        _x360.SetButtonState(Xbox360Button.Back, b.HasFlag(SwitchButtons.Minus));
        _x360.SetButtonState(Xbox360Button.Guide, b.HasFlag(SwitchButtons.Home));

        // D-pad
        _x360.SetButtonState(Xbox360Button.Up, b.HasFlag(SwitchButtons.Up));
        _x360.SetButtonState(Xbox360Button.Down, b.HasFlag(SwitchButtons.Down));
        _x360.SetButtonState(Xbox360Button.Left, b.HasFlag(SwitchButtons.Left));
        _x360.SetButtonState(Xbox360Button.Right, b.HasFlag(SwitchButtons.Right));

        // Triggers (ZL / ZR)
        byte lt = state.LeftTriggerAnalog > 0 ? state.LeftTriggerAnalog : (b.HasFlag(SwitchButtons.ZL) ? (byte)255 : (byte)0);
        byte rt = state.RightTriggerAnalog > 0 ? state.RightTriggerAnalog : (b.HasFlag(SwitchButtons.ZR) ? (byte)255 : (byte)0);
        _x360.SetSliderValue(Xbox360Slider.LeftTrigger, lt);
        _x360.SetSliderValue(Xbox360Slider.RightTrigger, rt);

        // Analog Sticks (-1.0..+1.0 -> -32768..32767)
        short lx = (short)Math.Clamp((int)(state.LeftStickX * 32767f), -32768, 32767);
        short ly = (short)Math.Clamp((int)(state.LeftStickY * 32767f), -32768, 32767);
        short rx = (short)Math.Clamp((int)(state.RightStickX * 32767f), -32768, 32767);
        short ry = (short)Math.Clamp((int)(state.RightStickY * 32767f), -32768, 32767);

        _x360.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
        _x360.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
        _x360.SetAxisValue(Xbox360Axis.RightThumbX, rx);
        _x360.SetAxisValue(Xbox360Axis.RightThumbY, ry);

        _x360.SubmitReport();
    }

    private void UpdateDualShock4(ControllerStateSnapshot state, SwitchButtons b)
    {
        if (_ds4 == null) return;

        // Face Buttons: In DS4, Cross is bottom (Nintendo B), Circle is right (Nintendo A), Square is left (Nintendo Y), Triangle is top (Nintendo X)
        _ds4.SetButtonState(DualShock4Button.Cross, b.HasFlag(SwitchButtons.B));
        _ds4.SetButtonState(DualShock4Button.Circle, b.HasFlag(SwitchButtons.A));
        _ds4.SetButtonState(DualShock4Button.Square, b.HasFlag(SwitchButtons.Y));
        _ds4.SetButtonState(DualShock4Button.Triangle, b.HasFlag(SwitchButtons.X));

        _ds4.SetButtonState(DualShock4Button.ShoulderLeft, b.HasFlag(SwitchButtons.L));
        _ds4.SetButtonState(DualShock4Button.ShoulderRight, b.HasFlag(SwitchButtons.R));
        _ds4.SetButtonState(DualShock4Button.ThumbLeft, b.HasFlag(SwitchButtons.LStick));
        _ds4.SetButtonState(DualShock4Button.ThumbRight, b.HasFlag(SwitchButtons.RStick));

        _ds4.SetButtonState(DualShock4Button.Options, b.HasFlag(SwitchButtons.Plus));
        _ds4.SetButtonState(DualShock4Button.Share, b.HasFlag(SwitchButtons.Minus) || b.HasFlag(SwitchButtons.Capture));

        byte special = 0;
        if (b.HasFlag(SwitchButtons.Home)) special |= (byte)DualShock4SpecialButton.Ps.Value;
        if (b.HasFlag(SwitchButtons.ChatC)) special |= (byte)DualShock4SpecialButton.Touchpad.Value;
        _ds4.SetSpecialButtonsFull(special);

        // D-Pad Hat
        bool u = b.HasFlag(SwitchButtons.Up);
        bool d = b.HasFlag(SwitchButtons.Down);
        bool l = b.HasFlag(SwitchButtons.Left);
        bool r = b.HasFlag(SwitchButtons.Right);

        DualShock4DPadDirection dpad = DualShock4DPadDirection.None;
        if (u && r) dpad = DualShock4DPadDirection.Northeast;
        else if (u && l) dpad = DualShock4DPadDirection.Northwest;
        else if (u) dpad = DualShock4DPadDirection.North;
        else if (d && r) dpad = DualShock4DPadDirection.Southeast;
        else if (d && l) dpad = DualShock4DPadDirection.Southwest;
        else if (d) dpad = DualShock4DPadDirection.South;
        else if (r) dpad = DualShock4DPadDirection.East;
        else if (l) dpad = DualShock4DPadDirection.West;

        _ds4.SetDPadDirection(dpad);

        // Triggers (0..255)
        byte lt = state.LeftTriggerAnalog > 0 ? state.LeftTriggerAnalog : (b.HasFlag(SwitchButtons.ZL) ? (byte)255 : (byte)0);
        byte rt = state.RightTriggerAnalog > 0 ? state.RightTriggerAnalog : (b.HasFlag(SwitchButtons.ZR) ? (byte)255 : (byte)0);
        _ds4.SetSliderValue(DualShock4Slider.LeftTrigger, lt);
        _ds4.SetSliderValue(DualShock4Slider.RightTrigger, rt);
        _ds4.SetButtonState(DualShock4Button.TriggerLeft, lt > 30);
        _ds4.SetButtonState(DualShock4Button.TriggerRight, rt > 30);

        // Sticks (0..255, center 128)
        byte lx = (byte)Math.Clamp((int)((state.LeftStickX + 1.0f) * 127.5f), 0, 255);
        byte ly = (byte)Math.Clamp((int)((-state.LeftStickY + 1.0f) * 127.5f), 0, 255); // Invert Y for standard DirectInput
        byte rx = (byte)Math.Clamp((int)((state.RightStickX + 1.0f) * 127.5f), 0, 255);
        byte ry = (byte)Math.Clamp((int)((-state.RightStickY + 1.0f) * 127.5f), 0, 255);

        _ds4.SetAxisValue(DualShock4Axis.LeftThumbX, lx);
        _ds4.SetAxisValue(DualShock4Axis.LeftThumbY, ly);
        _ds4.SetAxisValue(DualShock4Axis.RightThumbX, rx);
        _ds4.SetAxisValue(DualShock4Axis.RightThumbY, ry);

        _ds4.SubmitReport();
    }

    private void OnX360FeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
    {
        PublishRumble(e.LargeMotor, e.SmallMotor);

        if (e.LedNumber >= 0 && e.LedNumber <= 3)
        {
            PlayerLedChanged?.Invoke(e.LedNumber + 1);
        }
    }

    private void OnDs4FeedbackReceived(object sender, DualShock4FeedbackReceivedEventArgs e) =>
        PublishRumble(e.LargeMotor, e.SmallMotor);

    private void PublishRumble(byte largeMotor, byte smallMotor)
    {
        var (left, right) = HdRumbleEngine.FromLegacyAndImpulseTriggers(largeMotor, smallMotor);
        _lastLeft = left;
        _lastRight = right;
        Interlocked.Increment(ref _rumbleReceived);
        HdRumbleReceived?.Invoke(new StereoRumble(left, right) { Sustained = true });
    }
    public void Stop()
    {
        if (!_isRunning) return;

        try
        {
            if (_x360 != null)
            {
                _x360.FeedbackReceived -= OnX360FeedbackReceived;
                _x360.Disconnect();
                _x360 = null;
            }

            if (_ds4 != null)
            {
#pragma warning disable CS0618
                _ds4.FeedbackReceived -= OnDs4FeedbackReceived;
#pragma warning restore CS0618
                _ds4.Disconnect();
                _ds4 = null;
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Avviso disconnessione ViGEmBus: {ex.Message}");
        }
        finally
        {
            _isRunning = false;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
