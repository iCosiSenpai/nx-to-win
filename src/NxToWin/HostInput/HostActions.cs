using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NxToWin.Models;

namespace NxToWin.HostInput;

/// <summary>Windows-side actions a controller button can trigger (outside the Nintendo/game world).</summary>
public enum HostAction
{
    [Description("Invia al gioco")] PassThrough,
    [Description("Disattiva")] Disabled,

    [Description("Apri Game Bar")] XboxGameBar,
    [Description("Screenshot Game Bar")] GameBarScreenshot,
    [Description("Avvia / ferma registrazione")] GameBarToggleRecording,
    [Description("Salva ultimi 30 secondi")] GameBarRecordThat,
    [Description("Attiva / disattiva microfono")] GameBarMicToggle,
    [Description("Strumento di cattura")] SnippingTool,
    [Description("Stamp schermo intero")] PrintScreen,

    [Description("Menu Start")] StartMenu,
    [Description("Visualizzazione attività")] TaskView,
    [Description("Cambia finestra")] AltTab,
    [Description("Mostra desktop")] ShowDesktop,
    [Description("Schermo intero")] AltEnter,
    [Description("Chiudi app")] AltF4,
    [Description("Esc")] Escape,
    [Description("Gestione attività")] TaskManager,
    [Description("Tastiera su schermo")] OnScreenKeyboard,
    [Description("Centro notifiche")] NotificationCenter,
    [Description("Impostazioni rapide")] QuickSettings,
    [Description("Blocca PC")] LockPc,

    [Description("Alza volume")] VolumeUp,
    [Description("Abbassa volume")] VolumeDown,
    [Description("Disattiva audio")] VolumeMute,
    [Description("Riproduci / pausa")] MediaPlayPause,
    [Description("Traccia successiva")] MediaNext,
    [Description("Traccia precedente")] MediaPrev,

    [Description("Ricentra giroscopio")] RecenterGyro,
}

public static class HostActionExtensions
{
    public static string ToDisplay(this HostAction a)
    {
        var mi = typeof(HostAction).GetField(a.ToString());
        var attr = mi != null ? (DescriptionAttribute?)Attribute.GetCustomAttribute(mi, typeof(DescriptionAttribute)) : null;
        return attr?.Description ?? a.ToString();
    }

    /// <summary>True when the action is executed on Windows (the physical button is consumed, not sent to the game).</summary>
    public static bool IsHostSide(this HostAction a) => a is not HostAction.PassThrough;
}

/// <summary>Injects keyboard shortcuts via SendInput.</summary>
public static class KeyboardInjector
{
    private const ushort VK_LWIN = 0x5B, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_TAB = 0x09,
        VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_SNAPSHOT = 0x2C,
        VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF,
        VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi; // ensures correct union size on x64
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

    private static bool IsExtended(ushort vk) => vk is VK_LWIN or VK_SNAPSHOT or
        VK_VOLUME_MUTE or VK_VOLUME_DOWN or VK_VOLUME_UP or VK_MEDIA_NEXT or VK_MEDIA_PREV or VK_MEDIA_PLAY_PAUSE;

    /// <summary>Presses all keys in order, then releases them in reverse order.</summary>
    public static void Chord(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
            inputs[i] = Make(keys[i], false);
        for (int i = 0; i < keys.Length; i++)
            inputs[keys.Length + i] = Make(keys[keys.Length - 1 - i], true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Make(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0)
            }
        }
    };

    public static void Execute(HostAction action)
    {
        switch (action)
        {
            case HostAction.XboxGameBar: Chord(VK_LWIN, 'G'); break;
            case HostAction.GameBarScreenshot: Chord(VK_LWIN, VK_MENU, VK_SNAPSHOT); break;
            case HostAction.GameBarToggleRecording: Chord(VK_LWIN, VK_MENU, 'R'); break;
            case HostAction.GameBarRecordThat: Chord(VK_LWIN, VK_MENU, 'G'); break;
            case HostAction.GameBarMicToggle: Chord(VK_LWIN, VK_MENU, 'M'); break;
            case HostAction.SnippingTool: Chord(VK_LWIN, VK_SHIFT, 'S'); break;
            case HostAction.PrintScreen: Chord(VK_SNAPSHOT); break;
            case HostAction.StartMenu: Chord(VK_LWIN); break;
            case HostAction.TaskView: Chord(VK_LWIN, VK_TAB); break;
            case HostAction.AltTab: Chord(VK_MENU, VK_TAB); break;
            case HostAction.ShowDesktop: Chord(VK_LWIN, 'D'); break;
            case HostAction.AltEnter: Chord(VK_MENU, VK_RETURN); break;
            case HostAction.AltF4: Chord(VK_MENU, VK_F4); break;
            case HostAction.Escape: Chord(VK_ESCAPE); break;
            case HostAction.TaskManager: Chord(VK_CONTROL, VK_SHIFT, VK_ESCAPE); break;
            case HostAction.OnScreenKeyboard: Chord(VK_LWIN, VK_CONTROL, 'O'); break;
            case HostAction.NotificationCenter: Chord(VK_LWIN, 'N'); break;
            case HostAction.QuickSettings: Chord(VK_LWIN, 'A'); break;
            case HostAction.LockPc: Chord(VK_LWIN, 'L'); break;
            case HostAction.VolumeUp: Chord(VK_VOLUME_UP); break;
            case HostAction.VolumeDown: Chord(VK_VOLUME_DOWN); break;
            case HostAction.VolumeMute: Chord(VK_VOLUME_MUTE); break;
            case HostAction.MediaPlayPause: Chord(VK_MEDIA_PLAY_PAUSE); break;
            case HostAction.MediaNext: Chord(VK_MEDIA_NEXT); break;
            case HostAction.MediaPrev: Chord(VK_MEDIA_PREV); break;
        }
    }
}

/// <summary>Tap / hold binding for one physical button.</summary>
public sealed class HostButtonBinding
{
    public HostAction Tap { get; set; } = HostAction.PassThrough;
    public HostAction Hold { get; set; } = HostAction.PassThrough;
    public bool IsActive => Tap != HostAction.PassThrough || Hold != HostAction.PassThrough;
}

/// <summary>
/// Turns physical buttons into Windows actions with tap vs. long-press semantics.
///  • Tap  = released before the hold threshold.
///  • Hold = kept pressed for ≥ threshold (fires once, while still held).
/// If one side is "PassThrough" and the other is a Windows action, the game still gets the button:
///  - Tap=PassThrough  → on a short press a ~70 ms pulse of the button is sent to the game.
///  - Hold=PassThrough → after the threshold the button is held down in the game until released.
/// </summary>
public sealed class HostButtonProcessor
{
    private sealed class State
    {
        public bool Down;
        public long DownTicks;
        public bool HoldFired;
        public long PulseUntilTicks;
    }

    private readonly Dictionary<SwitchButtons, State> _states = new();
    private volatile bool _recenterRequested;
    private bool _extraRecenterHeld;
    private SwitchButtons _previousPhysical;

    public Dictionary<SwitchButtons, HostButtonBinding> Bindings { get; } = new();
    public int HoldThresholdMs { get; set; } = 450;

    public event Action<HostAction>? ActionTriggered;

    internal void Configure(ControllerProfile profile)
    {
        lock (_states)
        {
            bool changed = HoldThresholdMs != profile.HoldMs;
            HoldThresholdMs = profile.HoldMs;
            changed |= SetBinding(SwitchButtons.Capture, profile.CaptureTap, profile.CaptureHold);
            changed |= SetBinding(SwitchButtons.Home, profile.HomeTap, profile.HomeHold);
            bool hasChat = VirtualControllerCapabilities.HasChatButton(profile.Output);
            changed |= SetBinding(SwitchButtons.ChatC, hasChat ? profile.ChatTap : HostAction.PassThrough,
                hasChat ? profile.ChatHold : HostAction.PassThrough);
            if (changed) Reset();
        }
    }

    private bool SetBinding(SwitchButtons button, HostAction tap, HostAction hold)
    {
        var binding = Bindings[button];
        if (binding.Tap == tap && binding.Hold == hold) return false;
        binding.Tap = tap;
        binding.Hold = hold;
        return true;
    }

    public void Reset()
    {
        lock (_states)
        {
            _states.Clear();
            _recenterRequested = false;
            _extraRecenterHeld = false;
            _previousPhysical = SwitchButtons.None;
        }
    }

    public bool ExtraRecenterPressed(bool pressed)
    {
        lock (_states)
        {
            bool rising = pressed && !_extraRecenterHeld;
            _extraRecenterHeld = pressed;
            return rising;
        }
    }

    public bool ConsumeRecenterRequest()
    {
        if (_recenterRequested)
        {
            _recenterRequested = false;
            return true;
        }
        return false;
    }

    public static readonly SwitchButtons[] BindableButtons =
    [
        SwitchButtons.Capture, SwitchButtons.Home, SwitchButtons.ChatC, SwitchButtons.GL, SwitchButtons.GR,
        SwitchButtons.Minus, SwitchButtons.Plus, SwitchButtons.LStick, SwitchButtons.RStick
    ];

    public HostButtonProcessor()
    {
        foreach (var b in BindableButtons)
            Bindings[b] = new HostButtonBinding();

        // Sensible defaults requested by the user: Capture = screenshot (tap) / video (hold), Home hold = Game Bar
        Bindings[SwitchButtons.Capture] = new HostButtonBinding { Tap = HostAction.GameBarScreenshot, Hold = HostAction.GameBarToggleRecording };
        Bindings[SwitchButtons.Home] = new HostButtonBinding { Tap = HostAction.PassThrough, Hold = HostAction.XboxGameBar };
    }

    /// <summary>Processes one input frame. Returns the button mask that must be forwarded to the game.</summary>
    public SwitchButtons Process(SwitchButtons physical) => Process(physical, out _);

    internal SwitchButtons Process(SwitchButtons physical, out SwitchButtons pressed)
    {
        lock (_states)
        {
            pressed = physical & ~_previousPhysical;
            _previousPhysical = physical;
            return ProcessCore(physical);
        }
    }

    internal void Trigger(HostAction action)
    {
        lock (_states) Fire(action);
    }

    private SwitchButtons ProcessCore(SwitchButtons physical)
    {
        long now = Stopwatch.GetTimestamp();
        long holdTicks = (long)(HoldThresholdMs / 1000.0 * Stopwatch.Frequency);
        long pulseTicks = (long)(0.07 * Stopwatch.Frequency);
        SwitchButtons output = physical;

        foreach (var (button, binding) in Bindings)
        {
            if (!binding.IsActive) continue;
            if (!_states.TryGetValue(button, out var st))
                _states[button] = st = new State();

            bool pressed = physical.HasFlag(button);
            output &= ~button; // consumed unless re-added below

            if (pressed && !st.Down)
            {
                st.Down = true;
                st.DownTicks = now;
                st.HoldFired = false;
                st.PulseUntilTicks = 0;
            }
            else if (pressed && st.Down)
            {
                if (!st.HoldFired && now - st.DownTicks >= holdTicks)
                {
                    st.HoldFired = true;
                    Fire(binding.Hold);
                }
                if (st.HoldFired && binding.Hold == HostAction.PassThrough)
                    output |= button; // long press goes to the game
            }
            else if (!pressed && st.Down)
            {
                st.Down = false;
                if (!st.HoldFired)
                {
                    var action = now - st.DownTicks >= holdTicks ? binding.Hold : binding.Tap;
                    if (action == HostAction.PassThrough)
                        st.PulseUntilTicks = now + pulseTicks;
                    else
                        Fire(action);
                }
            }

            if (now < st.PulseUntilTicks)
                output |= button;
        }

        return output;
    }

    private void Fire(HostAction action)
    {
        if (action is HostAction.PassThrough or HostAction.Disabled) return;
        ActionTriggered?.Invoke(action);
        if (action == HostAction.RecenterGyro)
        {
            _recenterRequested = true;
            return;
        }
        Task.Run(() =>
        {
            try { KeyboardInjector.Execute(action); } catch { }
        });
    }
}
