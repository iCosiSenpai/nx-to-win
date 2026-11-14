namespace NxToWin.VirtualOutput;

internal static class SdlSwitch2Compatibility
{
    // These names come from the SDL 3 Switch 2 HIDAPI driver and libusb loader.
    // Inspect bytes without loading code from the selected game.
    public static bool HasDriver(ReadOnlySpan<byte> image) =>
        image.StartsWith("MZ"u8) &&
        image.IndexOf("SDL_JOYSTICK_HIDAPI_SWITCH2"u8) >= 0 &&
        image.IndexOf("libusb_init"u8) >= 0;
}
