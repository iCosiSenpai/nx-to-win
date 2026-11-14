using NxToWin.Dsp;

namespace NxToWin.Hardware;

// A single writer owns each transport write. Slow BLE writes retain the newest
// game packet and bound latency, while previews and stops share the same writer.
internal sealed class RumbleOutput(Action<string> log)
{
    private readonly record struct Request(IPhysicalController Target, StereoRumble Rumble, int Generation, long Deadline);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Request? _pending;
    private bool _draining;
    private int _generation;
    private IPhysicalController? _lastFailure;
    private IPhysicalController? _liveTarget;
    private StereoRumble _liveRumble = StereoRumble.Silence;
    private long _liveUntil;

    public StereoRumble GetLiveRumble(IPhysicalController? target)
    {
        lock (_gate)
            return target != null && _liveTarget == target && Environment.TickCount64 <= _liveUntil
                ? _liveRumble : StereoRumble.Silence;
    }

    public void QueueGame(IPhysicalController target, StereoRumble rumble)
    {
        lock (_gate)
        {
            // Legacy backends can go away without delivering the final zero packet.
            _pending = new(target, rumble, _generation, Environment.TickCount64 + 5000);
            if (_draining) return;
            _draining = true;
            _ = Task.Run(DrainAsync);
        }
    }

    public void ClearGame()
    {
        lock (_gate)
        {
            _generation++;
            _pending = null;
            _liveTarget = null;
            _liveRumble = StereoRumble.Silence;
        }
    }

    public async Task SendAsync(IPhysicalController target, StereoRumble rumble)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try { await target.SendHdRumbleAsync(rumble).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }

    public async Task StopAsync(IPhysicalController? target)
    {
        if (target is not { IsConnected: true, SupportsRumble: true }) return;
        try { await SendAsync(target, StereoRumble.Silence).ConfigureAwait(false); }
        catch (Exception ex) { log($"Arresto vibrazione · {target.Name}: {ex.Message}"); }
    }

    private async Task DrainAsync()
    {
        Request? hold = null;
        while (true)
        {
            Request request;
            lock (_gate)
            {
                var next = _pending ?? hold;
                if (next is not { } queued || queued.Generation != _generation)
                {
                    _draining = false;
                    return;
                }
                request = queued;
                _pending = null;
            }

            hold = null;
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (request.Generation != _generation || !request.Target.IsConnected) continue;
                }
                var rumble = request.Rumble.Sustained && Environment.TickCount64 >= request.Deadline
                    ? StereoRumble.Silence : request.Rumble;
                await request.Target.SendHdRumbleAsync(rumble.Sustained ? rumble.FillPacket() : rumble).ConfigureAwait(false);
                lock (_gate)
                {
                    if (request.Generation == _generation)
                    {
                        _liveRumble = rumble;
                        _liveTarget = request.Target;
                        _liveUntil = Environment.TickCount64 + 85;
                    }
                }
                _lastFailure = null;
                if (rumble.Sustained && !rumble.IsSilent) hold = request;
            }
            catch (Exception ex)
            {
                if (_lastFailure != request.Target)
                    log($"Vibrazione · {request.Target.Name}: {ex.Message}");
                _lastFailure = request.Target;
            }
            finally { _writer.Release(); }

            if (hold != null) await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
