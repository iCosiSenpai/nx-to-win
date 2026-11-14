using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using NxToWin.Models;

namespace NxToWin.Dsp;

/// <summary>
/// 6-axis Mahony AHRS with automatic gyro bias tracking.
///
/// Why yaw used to "keep climbing": with only gyro + accelerometer, yaw (rotation around gravity)
/// is unobservable — any residual gyro bias is integrated forever. This engine:
///   1. continuously detects when the controller is at rest (low angular rate + accel ≈ 1 g + stable accel)
///      and slowly learns the gyro bias in that state (no manual calibration needed, tracks thermal drift);
///   2. forwards angular velocity independently of the orientation filter;
///   3. normalises the forwarded raw gyro to Pro-Controller units (14.2857 LSB/dps) so Joy-Con 2
///      (16.384 LSB/dps) and Pro data look identical to every game.
/// </summary>
public sealed class ImuFusionEngine
{
    public const float AccelLsbPerG = 4096.0f;
    public const float GyroLsbPerDpsPro = 14.285714f;
    public const float GyroLsbPerDpsJoyCon = 16.384f;
    internal const float Switch2ProReportGyroCountsPerDps = short.MaxValue * (MathF.PI / 180f) / 34.8f;
    internal const float JoyCon2ReportGyroCountsPerDps = short.MaxValue * (MathF.PI / 180f) / 40f;

    // Rest detection tuning
    private const float RestGyroThresholdDps = 0.5f;
    private const float RestAccelNormTolerance = 0.06f; // | |a| - 1g |
    private const float RestAccelJitterG = 0.02f;       // per-sample accel change
    private const double RestTimeToLearnSec = 0.35;     // stillness required before learning bias

    private float _q0 = 1f, _q1, _q2, _q3;
    private float? _magneticHeading;
    private float _magneticMagnitude;
    private long _lastTicks;

    private float _prevAx, _prevAy, _prevAz = 1f;
    private double _restSeconds;
    private bool _biasInitialised;
    private int _learnSamples;

    public float BiasX { get; set; }
    public float BiasY { get; set; }
    public float BiasZ { get; set; }

    public float AccelG_X { get; private set; }
    public float AccelG_Y { get; private set; }
    public float AccelG_Z { get; private set; } = 1.0f;
    public float GyroDps_X { get; private set; }
    public float GyroDps_Y { get; private set; }
    public float GyroDps_Z { get; private set; }
    public float PitchDeg { get; private set; }
    public float RollDeg { get; private set; }
    public float YawDeg { get; private set; }
    public bool IsAtRest { get; private set; }
    public bool AutoBiasTracking { get; set; } = true;

    private volatile bool _isCalibrating;
    private int _recenterRequested;
    private int _calibrationTargetSamples;
    private readonly List<(short X, short Y, short Z)> _calibrationSamples = new();

    public bool IsCalibrating => _isCalibrating;

    public void StartGyroCalibration(int sampleCount = 150)
    {
        lock (_calibrationSamples)
        {
            _calibrationSamples.Clear();
            _calibrationTargetSamples = sampleCount;
            _isCalibrating = true;
        }
    }

    /// <summary>Resets orientation (yaw = 0) without touching the learned bias.</summary>
    public void Recenter()
        => Interlocked.Exchange(ref _recenterRequested, 1);

    private void RecenterHeading()
    {
        float yaw = MathF.Atan2(2f * (_q0 * _q3 + _q1 * _q2), 1f - 2f * (_q2 * _q2 + _q3 * _q3));
        var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -yaw);
        var orientation = heading * new Quaternion(_q1, _q2, _q3, _q0);
        (_q0, _q1, _q2, _q3) = (orientation.W, orientation.X, orientation.Y, orientation.Z);
        _magneticHeading = null;
    }

    public void ProcessSample(ControllerStateSnapshot state, bool isProController, bool use9AxisMagnetometer = true) =>
        ProcessSample(state, isProController ? GyroLsbPerDpsPro : GyroLsbPerDpsJoyCon, use9AxisMagnetometer);

    internal void ProcessSample(ControllerStateSnapshot state, float gyroScale, bool use9AxisMagnetometer)
    {
        if (Interlocked.Exchange(ref _recenterRequested, 0) != 0)
            RecenterHeading();
        long now = Stopwatch.GetTimestamp();
        float dt = _lastTicks == 0 ? 0.005f : (float)(now - _lastTicks) / Stopwatch.Frequency;
        _lastTicks = now;
        dt = Math.Clamp(dt, 0.001f, 0.05f);

        // ---- Manual calibration (button) ----
        if (_isCalibrating)
        {
            lock (_calibrationSamples)
            {
                _calibrationSamples.Add((state.GyroX, state.GyroY, state.GyroZ));
                if (_calibrationSamples.Count >= _calibrationTargetSamples)
                {
                    double sx = 0, sy = 0, sz = 0;
                    foreach (var s in _calibrationSamples) { sx += s.X; sy += s.Y; sz += s.Z; }
                    int n = _calibrationSamples.Count;
                    BiasX = (float)(sx / n);
                    BiasY = (float)(sy / n);
                    BiasZ = (float)(sz / n);
                    _biasInitialised = true;
                    _isCalibrating = false;
                    RecenterHeading();
                }
            }
        }

        float ax = state.AccelX / AccelLsbPerG;
        float ay = state.AccelY / AccelLsbPerG;
        float az = state.AccelZ / AccelLsbPerG;
        float accelNorm = MathF.Sqrt(ax * ax + ay * ay + az * az);

        float gxDps = (state.GyroX - BiasX) / gyroScale;
        float gyDps = (state.GyroY - BiasY) / gyroScale;
        float gzDps = (state.GyroZ - BiasZ) / gyroScale;

        // ---- Rest detection & automatic bias learning ----
        float jitter = MathF.Max(MathF.Abs(ax - _prevAx), MathF.Max(MathF.Abs(ay - _prevAy), MathF.Abs(az - _prevAz)));
        _prevAx = ax; _prevAy = ay; _prevAz = az;

        float rateGate = _biasInitialised ? RestGyroThresholdDps : 1f;
        bool stillNow = gxDps * gxDps + gyDps * gyDps + gzDps * gzDps < rateGate * rateGate &&
                        MathF.Abs(accelNorm - 1f) < RestAccelNormTolerance && jitter < RestAccelJitterG;

        _restSeconds = stillNow ? _restSeconds + dt : 0;
        IsAtRest = _restSeconds >= RestTimeToLearnSec;

        if (IsAtRest && AutoBiasTracking && !_isCalibrating)
        {
            // Fast convergence for the first ~1 s of stillness, then a slow thermal-drift tracker.
            float alpha = _learnSamples < 200 ? 0.05f : 0.004f;
            _learnSamples++;
            BiasX += alpha * (state.GyroX - BiasX);
            BiasY += alpha * (state.GyroY - BiasY);
            BiasZ += alpha * (state.GyroZ - BiasZ);
            if (_learnSamples >= 60) _biasInitialised = true;

            gxDps *= 1f - alpha;
            gyDps *= 1f - alpha;
            gzDps *= 1f - alpha;
        }

        // Forward bias-corrected gyro in Pro-Controller units for virtual pads.
        state.GyroX = ToShort(gxDps * GyroLsbPerDpsPro);
        state.GyroY = ToShort(gyDps * GyroLsbPerDpsPro);
        state.GyroZ = ToShort(gzDps * GyroLsbPerDpsPro);
        state.GyroDps_X = gxDps;
        state.GyroDps_Y = gyDps;
        state.GyroDps_Z = gzDps;
        state.AccelG_X = ax;
        state.AccelG_Y = ay;
        state.AccelG_Z = az;

        const float deg2Rad = MathF.PI / 180.0f;
        float yawCorrection = use9AxisMagnetometer ? GetMagneticYawCorrection(state) : 0;
        if (!use9AxisMagnetometer) _magneticHeading = null;
        UpdateOrientation(gxDps * deg2Rad, gyDps * deg2Rad, gzDps * deg2Rad, ax, ay, az, yawCorrection, dt);

        float sinrCosp = 2f * (_q0 * _q1 + _q2 * _q3);
        float cosrCosp = 1f - 2f * (_q1 * _q1 + _q2 * _q2);
        state.RollDeg = MathF.Atan2(sinrCosp, cosrCosp) * (180f / MathF.PI);

        float sinp = 2f * (_q0 * _q2 - _q3 * _q1);
        state.PitchDeg = (MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp)) * (180f / MathF.PI);

        float sinyCosp = 2f * (_q0 * _q3 + _q1 * _q2);
        float cosyCosp = 1f - 2f * (_q2 * _q2 + _q3 * _q3);
        state.YawDeg = MathF.Atan2(sinyCosp, cosyCosp) * (180f / MathF.PI);

        AccelG_X = ax; AccelG_Y = ay; AccelG_Z = az;
        GyroDps_X = gxDps; GyroDps_Y = gyDps; GyroDps_Z = gzDps;
        PitchDeg = state.PitchDeg; RollDeg = state.RollDeg; YawDeg = state.YawDeg;
    }

    private static short ToShort(float v) =>
        (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);

    private float GetMagneticYawCorrection(ControllerStateSnapshot sample)
    {
        var magnetic = new System.Numerics.Vector3(sample.MagX, sample.MagY, sample.MagZ);
        float magnitude = magnetic.Length();
        if (magnitude is < 80 or > 1200) return 0;
        var orientation = new System.Numerics.Quaternion(_q1, _q2, _q3, _q0);
        var world = System.Numerics.Vector3.Transform(magnetic / magnitude, orientation);
        if (world.X * world.X + world.Y * world.Y < .04f) return 0;
        float heading = MathF.Atan2(world.Y, world.X);
        if (_magneticHeading is not float reference)
        {
            _magneticHeading = heading;
            _magneticMagnitude = magnitude;
            return 0;
        }
        if (MathF.Abs(magnitude - _magneticMagnitude) > _magneticMagnitude * .35f) return 0;
        float error = MathF.IEEERemainder(reference - heading, MathF.Tau);
        return Math.Clamp(error * .35f, -.35f, .35f);
    }

    private void UpdateOrientation(float gx, float gy, float gz, float ax, float ay, float az, float yawCorrection, float dt)
    {
        const float twoKp = 1.0f;
        // The Mahony feedback uses half vectors. A yaw rate is applied along the full gravity axis.
        float vx = _q1 * _q3 - _q0 * _q2;
        float vy = _q0 * _q1 + _q2 * _q3;
        float vz = _q0 * _q0 - 0.5f + _q3 * _q3;
        gx += 2 * vx * yawCorrection;
        gy += 2 * vy * yawCorrection;
        gz += 2 * vz * yawCorrection;

        float norm = MathF.Sqrt(ax * ax + ay * ay + az * az);
        if (norm > 0.1f && norm < 3.0f)
        {
            ax /= norm; ay /= norm; az /= norm;

            float ex = ay * vz - az * vy;
            float ey = az * vx - ax * vz;
            float ez = ax * vy - ay * vx;

            gx += twoKp * ex;
            gy += twoKp * ey;
            gz += twoKp * ez;
        }

        gx *= 0.5f * dt; gy *= 0.5f * dt; gz *= 0.5f * dt;

        float qa = _q0, qb = _q1, qc = _q2;
        _q0 += -qb * gx - qc * gy - _q3 * gz;
        _q1 += qa * gx + qc * gz - _q3 * gy;
        _q2 += qa * gy - qb * gz + _q3 * gx;
        _q3 += qa * gz + qb * gy - qc * gx;

        float qNorm = MathF.Sqrt(_q0 * _q0 + _q1 * _q1 + _q2 * _q2 + _q3 * _q3);
        if (qNorm > 1e-6f)
        {
            _q0 /= qNorm; _q1 /= qNorm; _q2 /= qNorm; _q3 /= qNorm;
        }
    }
}
