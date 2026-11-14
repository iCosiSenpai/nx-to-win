using System;
using System.Threading.Tasks;
using NxToWin.Dsp;
using NxToWin.Models;

namespace NxToWin.Hardware;

public interface IPhysicalController : IDisposable
{
    string Id { get; }
    string Name { get; }
    string TransportName { get; }
    ushort ProductId { get; }
    bool IsConnected { get; }
    bool IsSwitch2 { get; }
    bool IsProController { get; }
    bool IsLeftJoyCon { get; }
    bool IsRightJoyCon { get; }

    ControllerStateSnapshot LatestState { get; }
    ImuFusionEngine ImuFusion { get; }

    event Action<IPhysicalController, ControllerStateSnapshot>? StateUpdated;
    event Action<IPhysicalController>? Disconnected;

    bool SupportsRumble { get; }
    Task SendHdRumbleAsync(StereoRumble rumble);
    Task SetPlayerLedsAsync(int playerNumber);
    void TriggerGyroCalibration();
    void CenterSticks();
}
