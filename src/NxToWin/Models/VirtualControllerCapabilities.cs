namespace NxToWin.Models;

internal static class VirtualControllerCapabilities
{
    public static bool HasChatButton(EmulationMode mode) => mode == EmulationMode.Switch2Pro_NativeUSBIP;

    public static bool HasCapture(EmulationMode mode, bool captureAsTouchpad) => mode switch
    {
        EmulationMode.Switch2Pro_NativeUSBIP or EmulationMode.SwitchPro_NativeVHF or EmulationMode.ViGEm_DualShock4 => true,
        EmulationMode.DualSense_PS5_VHF => captureAsTouchpad,
        _ => false
    };

    public static bool HasHome(EmulationMode mode, bool xboxGuideFromHome) =>
        mode != EmulationMode.ViGEm_Xbox360 || xboxGuideFromHome;
}
