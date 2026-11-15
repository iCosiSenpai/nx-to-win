using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NxToWin.Dsp;
using NxToWin.Hardware;
using NxToWin.HostInput;
using NxToWin.Models;
using NxToWin.Updates;
using NxToWin.VirtualOutput;

CheckSwitch2UsbDescriptors();
CheckSwitch2Reports();
CheckBatteries();
CheckRumbleAndMotion();
CheckGyroMotion();
CheckSteamLaunchArguments();
CheckSdlAndOcarina();
CheckControllerMappings();
CheckFactoryProfiles();
CheckUpdates();
await CheckDsuAsync();
Console.WriteLine("USB, report Switch 2, batterie, vibrazione, fusione, aggiornamenti e DSU: verifiche riuscite.");

static void CheckUpdates()
{
    var current = new Version(0, 5, 0);
    string hash = new('a', 64);
    var asset = new GitHubUpdates.Asset("NxToWin.Desktop-0.5.1-full.nupkg", "uploaded", 12345, "sha256:" + hash);
    var release = new GitHubUpdates.Release("v0.5.1", false, false, true, [asset]);
    Require(GitHubUpdates.ReadAsset(release, current) is { SHA256: var sha, Size: 12345 } && sha == hash,
        "aggiornamento stabile immutabile e SHA-256");
    Require(GitHubUpdates.ReadAsset(release, new Version(0, 5, 1)) is null &&
        GitHubUpdates.ReadAsset(release, new Version(0, 6, 0)) is null, "aggiornamento solo a una versione più recente");
    foreach (var rejected in new[]
    {
        release with { Immutable = false },
        release with { Draft = true },
        release with { TagName = "v0.5.1-rc.1", Prerelease = true },
        release with { Assets = [asset with { Digest = null }] },
        release with { Assets = [asset with { Digest = "sha256:" + new string('z', 64) }] },
        release with { Assets = [asset with { Name = "different-package-0.5.1-full.nupkg" }] }
    })
    {
        bool invalid = false;
        try { _ = GitHubUpdates.ReadAsset(rejected, current); }
        catch (System.IO.InvalidDataException) { invalid = true; }
        Require(invalid, "metadati di aggiornamento non attendibili rifiutati");
    }
}

static void CheckSwitch2UsbDescriptors()
{
    byte configuration = 0;
    byte[] Read(byte type, ushort value, ushort index, byte length)
    {
        byte[] setup = [type, 6, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), length, 0];
        return Switch2UsbProtocol.GetControlReply(setup, length, ref configuration, [])
            ?? throw new InvalidOperationException("Descrittore non restituito.");
    }

    byte[] device = Read(0x80, 0x0100, 0, 18);
    Require(BinaryPrimitives.ReadUInt16LittleEndian(device.AsSpan(8)) == NintendoIds.VendorId, "VID");
    Require(BinaryPrimitives.ReadUInt16LittleEndian(device.AsSpan(10)) == NintendoIds.ProController2Pid, "PID");
    byte[] config = Read(0x80, 0x0200, 0, 255);
    Require(config.Length == BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(2)), "wTotalLength");
    var endpoints = new List<byte>();
    int reportLength = 0;
    for (int i = 0; i < config.Length; i += config[i])
    {
        Require(config[i] >= 2 && i + config[i] <= config.Length, "lunghezza descrittore secondario");
        if (config[i + 1] == 5) endpoints.Add(config[i + 2]);
        if (config[i + 1] == 0x21) reportLength = BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(i + 7));
    }
    Require(endpoints.SequenceEqual(new byte[] { 0x81, 0x01, 0x02, 0x82 }), "endpoint");
    Require(Read(0x81, 0x2200, 0, 255).Length == reportLength && reportLength == 97, "report HID");
    Require(Read(0x80, 0x03EE, 0, 18).Length == 18, "stringa MS OS 1.0");
    Require(System.Text.Encoding.ASCII.GetString(Read(0xC0, 0, 4, 40), 18, 6) == "WINUSB", "ID WinUSB");
    byte[] properties = Read(0xC1, 1, 5, 142);
    Require(properties.Length == BinaryPrimitives.ReadInt32LittleEndian(properties), "lunghezza proprietà WinUSB");
}

static void CheckSwitch2Reports()
{
    byte format = 9;
    bool streaming = false;
    Switch2UsbProtocol.CreateReply([3, 0x91, 0, 0x0A, 0, 4, 0, 0, 5, 0, 0, 0], ref format, ref streaming);
    Switch2UsbProtocol.CreateReply([3, 0x91, 0, 0x0D, 0, 0, 0, 0], ref format, ref streaming);
    Require(format == 5 && streaming, "selezione report SDL");
    byte[] flash = Switch2UsbProtocol.CreateReply([2, 0x91, 0, 1, 0, 8, 0, 0, 0, 0, 0, 0, 0x80, 0x30, 1, 0],
        ref format, ref streaming);
    Require(flash.Length == 80 && flash[1] == 1 && flash[8] == 64, "lettura blocco SPI");
    var calibration = StickCalibration.FromSwitch2SpiBytes(flash.AsSpan(16 + 0x28));
    Require(calibration.IsValid && calibration.CenterX == 2048 && calibration.MaxX == 1496, "calibrazione SPI");
    var state = new ControllerStateSnapshot { LeftStickX = .5f, RightStickY = -.5f, BatteryVoltage = 3.145f, GyroDps_X = 35 };
    var buttons = SwitchButtons.B | SwitchButtons.GL | SwitchButtons.ZL;
    foreach (byte id in new byte[] { 5, 9 })
    {
        byte[] raw = Switch2UsbProtocol.BuildInputReport(id, 12, state, buttons, suppressGyro: false);
        var parsed = Switch2InputReport.Parse(id, raw.AsSpan(1), NintendoIds.ProController2Pid, out bool motion);
        Require(parsed != null && parsed.Buttons == buttons, $"pulsanti report {id:X2}");
        Require(parsed!.RawLeftStickX == 2796 && parsed.RawRightStickY == 1300, $"stick report {id:X2}");
        Require(motion == (id == 5), $"movimento report {id:X2}");
    }
    Require(Switch2InputReport.Parse(5, new byte[15], NintendoIds.ProController2Pid, out _) == null, "rifiuto report troncato");
}

static void CheckBatteries()
{
    Require(BatteryReading.Switch1(0x80).Level.EndsWith("(4/4)"), "livello Switch 1");
    Require(BatteryReading.Switch1(0x10).Power == "In carica", "carica Switch 1");
    Require(BatteryReading.Switch2(3145, charge: 0).Level == "3145 mV", "tensione Switch 2");
    Require(BatteryReading.Switch2(0, 0x26).Level == "9/9", "livello compatto Switch 2");
    var charging = BatteryReading.Switch2(3870, charge: 0x34);
    Require(charging.Charging && charging.Millivolts == 3870 && charging.Percent == null, "carica Switch 2 senza percentuale stimata");
    Require(!BatteryReading.Switch2(3870, charge: 0x20).Charging, "carica Switch 2 non dedotta da byte ambiguo");
    var switch1 = BatteryReading.Switch1(0x51, 3760).WithWindowsPercentage(65);
    Require(switch1.Charging && switch1.ExternalPower && switch1.Level == "65%" && switch1.Voltage == "3760 mV",
        "percentuale Windows e stato HID indipendenti");
    Require(BatteryDiagnostics.CapacityLevel(300, 600) == .5.ToString("P0"), "rapporto capacità Windows");
    Require(BatteryDiagnostics.CapacityLevel(10, 0) == "—", "capacità Windows non valida");
    var ds4 = new byte[64];
    ds4[0] = 1;
    ds4[30] = 0x15;
    Require(BatteryDiagnostics.ParsePlayStation(0x09CC, ds4)?.Level == "50–59%", "DualShock 4 USB");
    var sense = new byte[78];
    sense[0] = 0x31;
    sense[54] = 0x21;
    Require(BatteryDiagnostics.ParsePlayStation(0x0DF2, sense)?.Power == "Carica completa", "DualSense Edge Bluetooth");
    Require(BatteryDiagnostics.ParsePlayStation(0x0DF2, sense.AsSpan(0, 12)) == null, "rifiuto report Sony troncato");
}

static void CheckRumbleAndMotion()
{
    byte[] example = [0x87, 0xC5, 0x21, 0x91, 0x38];
    var frame = VibrationFrame.FromSwitch2Bytes(example);
    Require(Math.Round(frame.HfFreq) == 167 && frame.HfAmp == 113 && Math.Round(frame.LfFreq) == 72 && frame.LfAmp == 226,
        "ordine delle bande Switch 2 SDL");
    Span<byte> encoded = stackalloc byte[5];
    frame.WriteSwitch2Bytes(encoded);
    Require(encoded.SequenceEqual(example), "round trip frequenze e ampiezze Switch 2");
    for (int code = 0; code < 1024; code++)
    {
        ulong packed = (uint)code | (ulong)(uint)code << 20;
        for (int i = 0; i < 5; i++) encoded[i] = (byte)(packed >> (8 * i));
        VibrationFrame.FromSwitch2Bytes(encoded).WriteSwitch2Bytes(encoded);
        Require((encoded[0] | ((encoded[1] & 3) << 8)) == code, $"round trip codice frequenza {code}");
    }
    var tone = new VibrationFrame(LfFreq: 220, LfAmp: 168, HfFreq: 440, HfAmp: 28);
    tone.WriteSwitch2Bytes(encoded);
    Require(encoded.SequenceEqual(new byte[] { 0x0D, 0x72, 0xD0, 0x1A, 0x2A }), "frequenze logaritmiche 440 e 220 Hz");
    var highTone = tone with { HfFreq = 880 };
    highTone.WriteSwitch2Bytes(encoded);
    Require(Math.Abs(VibrationFrame.FromSwitch2Bytes(encoded).HfFreq - 880) < 1, "frequenza Switch 2 oltre 511 Hz");
    Require(Math.Abs(HdRumbleEngine.DecodeSwitch1Side(HdRumbleEngine.EncodeSwitch1Side(highTone)).HfHz - 880) < 10,
        "gamma di frequenza Switch 1");
    byte[] switch1 = HdRumbleEngine.EncodeSwitch1Side(new VibrationFrame(160, .05f));
    var decoded = HdRumbleEngine.DecodeSwitch1Side(switch1);
    Require(decoded.LfAmp > 25 && Math.Abs(decoded.LfHz - 160) < 5, "ampiezza e frequenza Switch 1");
    Require(HdRumbleEngine.BuildGameCubeOutput(3, false, true)[2] == 2, "arresto vibrazione GameCube");
    var samples = new StereoRumble(new MotorSamples(frame, tone, highTone, 3), new MotorSamples(tone));
    byte[] report = HdRumbleEngine.BuildSwitch2Report(9, samples, wired: true, leftJoyCon: false, rightJoyCon: false);
    Require(report.Length == 64 && report[1] == 0x79 && report[17] == 0x59, "conteggi separati e sequenza USB");
    Require(StereoRumble.TryReadSwitch2Report(report, out var decodedReport) &&
        decodedReport.Left.Count == 3 && Math.Abs(decodedReport.Left.Third.HfFreq - 880) < 1 && decodedReport.Right.Count == 1,
        "inoltro del terzo campione Switch 2");
    byte[] right = HdRumbleEngine.BuildSwitch2Report(9, samples, wired: false, leftJoyCon: false, rightJoyCon: true);
    Require(right.Length == 17 && right[1] == 0x59 && right.AsSpan(2, 5).SequenceEqual(report.AsSpan(18, 5)),
        "canale Joy-Con 2 destro");
    var limited = new VibrationFrame(LfAmp: 820, HfAmp: 410).WithStrength(2.5);
    Require(limited.LfAmp + limited.HfAmp <= 1000 && Math.Abs(limited.LfAmp - 2 * limited.HfAmp) <= 1,
        "limite combinato e proporzioni tra bande");
    var (leftLow, rightLow) = HdRumbleEngine.FromLegacyAndImpulseTriggers(255, 0);
    var (leftHigh, rightHigh) = HdRumbleEngine.FromLegacyAndImpulseTriggers(0, 255);
    Require(leftLow.LfAmp > rightLow.LfAmp && rightHigh.HfAmp > leftHigh.HfAmp &&
        leftLow.HfAmp == 0 && rightHigh.LfAmp == 0, "separazione dei motori ViGEm");
    Require(HapticEffects.Build("chirp").Max(step => step.Left.LfFreq) == 420, "gamma dichiarata del chirp");

    var fusion = new ImuFusionEngine();
    fusion.ProcessSample(new() { AccelZ = 4096, MagX = 300 }, true);
    ControllerStateSnapshot sample = new();
    for (int i = 0; i < 150; i++)
    {
        sample = new() { AccelZ = 4096, MagY = 300 };
        fusion.ProcessSample(sample, true);
    }
    Require(sample.YawDeg < -1, "correzione magnetica di yaw");
}

static void CheckSdlAndOcarina()
{
    Require(SdlSwitch2Compatibility.HasDriver("MZ SDL_JOYSTICK_HIDAPI_SWITCH2 libusb_init"u8), "driver SDL Switch 2");
    Require(!SdlSwitch2Compatibility.HasDriver("MZ libusb_init"u8), "SDL standard senza driver Switch 2");
    Require(!SdlSwitch2Compatibility.HasDriver("SDL_JOYSTICK_HIDAPI_SWITCH2 libusb_init"u8), "file SDL non eseguibile");

    CheckTrack("lullaby", [494, 587, 440, 494, 587, 440], [0, 850, 1267, 2567, 3417, 3833, 5500], [392, 440, 494, 587, 880]);
    CheckTrack("time", [440, 294, 349, 440, 294, 349], [0, 533, 1617, 2167, 2700, 3783, 5433], [440, 523, 494, 392, 349]);
    CheckTrack("storms", [294, 349, 587, 294, 349, 587], [0, 183, 367, 1117, 1300, 1483, 2983], [659, 698, 659, 698, 659]);
    var hero = OcarinaHaptics.Build("hero");
    Require(hero.Sum(step => step.Milliseconds) >= 25_000 &&
        hero.All(step => step.Milliseconds > 0 && step.Left == step.Right) &&
        hero[^1].Left.IsSilent, "tema eroico completo e arresto in silenzio");
    Require(NotePitches(hero).Take(12).SequenceEqual([392, 294, 392, 392, 440, 494, 523, 587, 587, 587, 622, 698]),
        "tema eroico con la salita e le terzine originali");
    Require(hero.Take(5).Sum(step => step.Milliseconds) == 300 && hero[5].Left.IsSilent && hero[5].Milliseconds == 300 &&
        hero.Skip(6).Take(5).Sum(step => step.Milliseconds) == 900, "tema eroico a 100 BPM con pausa di croma");
    Require(OcarinaHaptics.Build("unrecognized").Length == 0, "titolo aptico sconosciuto");

    var profile = new BridgeConfiguration { SlAction = ExtraButtonAction.MapToA };
    var mapped = ButtonRemapper.ApplyRemapping(new() { Buttons = SwitchButtons.SL_L }, profile, out _, out _);
    Require(mapped == SwitchButtons.A, "rimappatura Joy-Con SL");
}

static void CheckGyroMotion()
{
    var fusion = new ImuFusionEngine();
    var sample = new ControllerStateSnapshot { AccelZ = 4096, GyroX = 15, GyroY = -17, GyroZ = 20 };
    for (int i = 0; i < 600; i++)
    {
        sample = new() { AccelZ = 4096, GyroX = 15, GyroY = -17, GyroZ = 20 };
        fusion.ProcessSample(sample, ImuFusionEngine.Switch2ProReportGyroCountsPerDps, false);
    }
    Require(Math.Abs(sample.GyroDps_X - .91f) < .03f && Math.Abs(sample.GyroDps_Y + 1.03f) < .03f &&
        Math.Abs(sample.GyroDps_Z - 1.22f) < .03f, "rotazione lenta combinata preservata");
    var fine = new ControllerStateSnapshot { AccelZ = 4096, GyroX = 3, GyroY = 3, GyroZ = 3 };
    fusion.ProcessSample(fine, ImuFusionEngine.Switch2ProReportGyroCountsPerDps, false);
    Require(fine.GyroDps_X > 0 && fine.GyroDps_Y > 0 && fine.GyroDps_Z > 0, "nessuna soglia indipendente sugli assi");
    byte[] packet = Switch2UsbProtocol.BuildInputReport(5, 1, sample, SwitchButtons.None, false);
    var decoded = Switch2InputReport.Parse(5, packet.AsSpan(1), NintendoIds.ProController2Pid, out bool motion);
    Require(motion && decoded is { GyroX: 15, GyroY: -17, GyroZ: 20 }, "scala e segni delle tre componenti USB/IP");
}

static void CheckSteamLaunchArguments()
{
    Require(SteamConflictGuard.HasNoJoyArgument("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -NOJOY -silent"), "Steam avviato esternamente con -nojoy");
    Require(!SteamConflictGuard.HasNoJoyArgument("steam.exe -nojoystick -somevalue \"a -nojoy b\""), "argomento Steam distinto");
    Require(SteamConflictGuard.IsSwitch2ConnectProcess("Switch2Connect") &&
        SteamConflictGuard.IsSwitch2ConnectProcess("Switch2Connect_v2.10") &&
        !SteamConflictGuard.IsSwitch2ConnectProcess("Switch2ConnectUpdater"), "riconoscimento del client con versione");
}

static void CheckTrack(string name, int[] frequencies, int[] boundaries, int[] answer)
{
    var steps = OcarinaHaptics.Build(name);
    int phraseLength = frequencies.Length * 5;
    int[] amplitudes = [336, 352, 320, 336, 352, 320];
    for (int i = 0; i < frequencies.Length; i++)
    {
        var note = steps.AsSpan(5 * i, 5).ToArray();
        var original = new VibrationFrame(LfFreq: Math.Round(frequencies[i] / 2d, MidpointRounding.AwayFromZero),
            LfAmp: amplitudes[i], HfFreq: frequencies[i], HfAmp: amplitudes[i] / 5);
        Require(note.SequenceEqual(new[]
        {
            (original.WithStrength(.48f), original.WithStrength(.48f), 15),
            (original, original, boundaries[i + 1] - boundaries[i] - 45),
            (original.WithStrength(.4f), original.WithStrength(.4f), 15),
            (original.WithStrength(.15f), original.WithStrength(.15f), 10),
            (VibrationFrame.Silence, VibrationFrame.Silence, 5)
        }), $"campioni, ampiezza e tempi originali · {name}, nota {i + 1}");
    }
    var continuation = steps[phraseLength..];
    int[] pitches = NotePitches(continuation).ToArray();
    Require(pitches.Length > answer.Length && pitches.Take(answer.Length).SequenceEqual(answer), $"risposta melodica · {name}");
    Require(!Enumerable.Range(0, pitches.Length - frequencies.Length + 1)
        .Any(i => pitches.AsSpan(i, frequencies.Length).SequenceEqual(frequencies)), $"l’incipit è suonato una volta · {name}");
    Require(steps.Sum(step => step.Milliseconds) >= 18_000 && steps[^1].Left.IsSilent &&
        steps.All(step => step.Milliseconds > 0 && step.Left == step.Right), $"traccia estesa e arresto in silenzio · {name}");
}

static IEnumerable<int> NotePitches((VibrationFrame Left, VibrationFrame Right, int Milliseconds)[] steps)
{
    for (int i = 0; i < steps.Length; i++)
    {
        if (steps[i].Left.IsSilent) continue;
        yield return (int)steps[i + 1].Left.HfFreq;
        i += 4;
    }
}

static void CheckControllerMappings()
{
    foreach (ushort pid in new[] { NintendoIds.ProController2Pid, NintendoIds.JoyCon2RightPid, NintendoIds.NsoGameCubePid })
        Require(NintendoIds.ConfigurableButtons(pid).HasFlag(SwitchButtons.ChatC), $"Chat C presente su {pid:X4}");
    foreach (ushort pid in new[] { NintendoIds.ProController1Pid, NintendoIds.JoyCon1RightPid, NintendoIds.JoyCon2LeftPid })
        Require(!NintendoIds.ConfigurableButtons(pid).HasFlag(SwitchButtons.ChatC), $"Chat C assente su {pid:X4}");

    var config = new BridgeConfiguration();
    var chat = new ControllerProfile { Output = EmulationMode.Switch2Pro_NativeUSBIP, ChatTap = HostAction.RecenterGyro };
    config.HostButtons.Configure(chat);
    ButtonRemapper.ApplyRemapping(new() { Buttons = SwitchButtons.ChatC }, config, out bool recenter, out _);
    Require(!recenter, "Chat C aspetta il rilascio");
    ButtonRemapper.ApplyRemapping(new(), config, out recenter, out _);
    Require(recenter, "Chat C ricentra al rilascio");
    ButtonRemapper.ApplyRemapping(new(), config, out recenter, out _);
    Require(!recenter, "Chat C ricentra una sola volta");
    foreach (var mode in Enum.GetValues<EmulationMode>().Where(mode => !VirtualControllerCapabilities.HasChatButton(mode)))
    {
        config.HostButtons.Configure(chat with { Output = mode });
        ButtonRemapper.ApplyRemapping(new() { Buttons = SwitchButtons.ChatC }, config, out _, out _);
        ButtonRemapper.ApplyRemapping(new(), config, out recenter, out _);
        Require(!recenter, $"Chat C inattiva con {mode}");
    }
    Require(WinUHidDualSenseVirtualDevice.MapStickToTouch(-1, 1) == (0, 0) &&
        WinUHidDualSenseVirtualDevice.MapStickToTouch(1, -1) == (1919, 1079), "coordinate touchpad DualSense");
}

static void CheckFactoryProfiles()
{
    Require(FactoryProfile.All.Count == 9 && FactoryProfile.All.Select(profile => profile.Id).Distinct().Count() == 9,
        "preset univoci con coppie Joy-Con");
    var joyCon = FactoryProfile.Find(NintendoIds.JoyCon2LeftPid)!;
    var pair = FactoryProfile.Find(NintendoIds.JoyCon2LeftPid, NintendoIds.JoyCon2RightPid)!;
    var pro = FactoryProfile.Find(NintendoIds.ProController2Pid)!;
    Require(joyCon.AllowsOpticalMouse && !joyCon.Settings.AutoMergeJoyCons && !joyCon.HasRightStick &&
        pair.Settings.AutoMergeJoyCons && pair.HasRightStick && pair.Buttons.HasFlag(SwitchButtons.ChatC),
        "preset singolo e coppia Joy-Con 2");
    Require(!pro.AllowsOpticalMouse && pro.Settings.Gl == ExtraButtonAction.MapToL3 &&
        pro.Settings.Gr == ExtraButtonAction.MapToR3, "preset Pro 2 con GL e GR");

    var previous = System.Globalization.CultureInfo.CurrentUICulture;
    try
    {
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Require(NxToWin.LocExtension.Get("Tema eroico di Zelda") == "Zelda Overworld Theme", "risorse inglesi caricate");
    }
    finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
}

static async Task CheckDsuAsync()
{
    using var server = new CemuhookDsuServer();
    Require(server.Start(0), "binding UDP");
    using var client = new UdpClient();
    client.Connect(IPAddress.Loopback, server.Port);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await client.SendAsync(DsuRequest(0x100000, []));
    var version = (await client.ReceiveAsync(timeout.Token)).Buffer;
    Require(version.Length == 22 && BinaryPrimitives.ReadUInt16LittleEndian(version.AsSpan(20)) == 1001, "versione DSU");

    byte[] slotQuery = DsuRequest(0x100001, [1, 0, 0, 0, 0]);
    await client.SendAsync(slotQuery);
    var disconnected = (await client.ReceiveAsync(timeout.Token)).Buffer;
    Require(disconnected[21] == 0 && disconnected[30] == 0, "slot DSU vuoto");
    await client.SendAsync(DsuRequest(0x100002, [0, 0, 0, 0, 0, 0, 0, 0]));
    await client.SendAsync(slotQuery);
    _ = await client.ReceiveAsync(timeout.Token);
    server.BroadcastControllerState(new RoutedInput(new ControllerStateSnapshot
    {
        LeftTriggerAnalog = 75, RightTriggerAnalog = 103, Battery = BatteryReading.Switch1(0x50), GyroDps_X = 22
    }, SwitchButtons.A, SuppressGyro: true));
    var report = (await client.ReceiveAsync(timeout.Token)).Buffer;
    Require(report.Length == 100 && report[31] == 1 && report[41] == 128, "stato DSU connesso");
    Require(report[30] == 0xEE && report[54] == 103 && report[55] == 75, "batteria e grilletti DSU");
    Require(report[50] == 255 && BinaryPrimitives.ReadSingleLittleEndian(report.AsSpan(88)) == 0, "mappatura e sospensione gyro DSU");
    server.BroadcastControllerState(null);
    Require((await client.ReceiveAsync(timeout.Token)).Buffer[31] == 0, "disconnessione DSU");
}

static byte[] DsuRequest(uint message, byte[] payload)
{
    byte[] request = new byte[20 + payload.Length];
    "DSUC"u8.CopyTo(request);
    BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), 1001);
    BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)(request.Length - 16));
    BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), message);
    payload.CopyTo(request, 20);
    return request;
}

static void Require(bool result, string check)
{
    if (!result) throw new InvalidOperationException($"Verifica fallita: {check}");
}
