using System.IO;
using System.Text.Json;
using NxToWin.HostInput;

namespace NxToWin.Models;

internal sealed record ControllerProfile
{
    public EmulationMode Output { get; init; } = EmulationMode.ViGEm_Xbox360;
    public AbxyLayout Layout { get; init; } = AbxyLayout.Nintendo;
    public ExtraButtonAction Gl { get; init; } = ExtraButtonAction.MapToL3;
    public ExtraButtonAction Gr { get; init; } = ExtraButtonAction.MapToR3;
    public ExtraButtonAction Sl { get; init; } = ExtraButtonAction.MapToL;
    public ExtraButtonAction Sr { get; init; } = ExtraButtonAction.MapToR;
    public HostAction CaptureTap { get; init; } = HostAction.GameBarScreenshot;
    public HostAction CaptureHold { get; init; } = HostAction.GameBarToggleRecording;
    public HostAction HomeTap { get; init; } = HostAction.PassThrough;
    public HostAction HomeHold { get; init; } = HostAction.XboxGameBar;
    public HostAction ChatTap { get; init; } = HostAction.RecenterGyro;
    public HostAction ChatHold { get; init; } = HostAction.GameBarMicToggle;
    public int HoldMs { get; init; } = 450;
    public float Rumble { get; init; } = 1.15f;
    public float LeftDeadzone { get; init; } = 0.05f;
    public float RightDeadzone { get; init; } = 0.05f;
    public bool SteamGuard { get; init; } = true;
    public bool OpticalMouse { get; init; }
    public float OpticalMouseSensitivity { get; init; } = 1.2f;
    public bool Magnetometer { get; init; } = true;
    public bool AutoMergeJoyCons { get; init; } = true;
    public bool EnableDsu { get; init; } = true;
    public bool CaptureAsTouchpad { get; init; } = true;
    public bool TouchpadFromStick { get; init; }
    public bool DualSenseMotion { get; init; } = true;
    public bool DualSenseTriggerRumble { get; init; } = true;
    public bool XboxOneTriggerRumble { get; init; } = true;
    public bool XboxGuideFromHome { get; init; } = true;
    public bool Ds4CaptureAsTouchpad { get; init; }
}

internal sealed class ControllerProfiles
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NxToWin", "profiles.json");

    public string? ActiveName { get; set; }
    public string? ActiveFactoryId { get; set; }
    public ControllerProfile Draft { get; set; } = new();
    public Dictionary<string, ControllerProfile> Named { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Assignments { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ControllerProfiles Load() => File.Exists(PathName)
        ? JsonSerializer.Deserialize<ControllerProfiles>(File.ReadAllText(PathName)) ?? new()
        : new();

    public void Save()
    {
        string path = PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
