using System.Runtime.InteropServices;
using NxToWin.Hardware;
using NxToWin.Models;

namespace NxToWin.HostInput;

internal sealed class OpticalMouse
{
    private readonly Dictionary<IPhysicalController, (ushort X, ushort Y)> _previous = new();

    public void Reset() => _previous.Clear();

    public void Update(IPhysicalController controller, float sensitivity)
    {
        if (controller.ProductId is not (NintendoIds.JoyCon2LeftPid or NintendoIds.JoyCon2RightPid)) return;
        var state = controller.LatestState;
        var sample = (X: state.MouseRawX, Y: state.MouseRawY);
        bool hadSample = _previous.TryGetValue(controller, out var previous);
        _previous[controller] = sample;
        if (!hadSample) return;

        int dx = (short)(sample.X - previous.X);
        int dy = (short)(sample.Y - previous.Y);
        if (Math.Abs(dx) >= 1200 || Math.Abs(dy) >= 1200) return;
        int x = (int)Math.Round(dx * sensitivity * .25);
        int y = (int)Math.Round(dy * sensitivity * .25);
        if (x != 0 || y != 0) mouse_event(1, x, y, 0, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extraInfo);
}
