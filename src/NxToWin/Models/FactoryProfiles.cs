using NxToWin.HostInput;

namespace NxToWin.Models;

internal sealed record FactoryProfile(string Id, ushort ProductId, string Label, ushort PartnerId = 0)
{
    public SwitchButtons Buttons => NintendoIds.ConfigurableButtons(ProductId) |
        (PartnerId == 0 ? SwitchButtons.None : NintendoIds.ConfigurableButtons(PartnerId));

    public bool AllowsOpticalMouse => ProductId is NintendoIds.JoyCon2LeftPid or NintendoIds.JoyCon2RightPid;
    public bool HasLeftStick => PartnerId != 0 || !NintendoIds.IsRightJoyCon(ProductId);
    public bool HasRightStick => PartnerId != 0 || !NintendoIds.IsLeftJoyCon(ProductId);

    public ControllerProfile Settings => new()
    {
        Gl = Buttons.HasFlag(SwitchButtons.GL) ? ExtraButtonAction.MapToL3 : ExtraButtonAction.None,
        Gr = Buttons.HasFlag(SwitchButtons.GR) ? ExtraButtonAction.MapToR3 : ExtraButtonAction.None,
        CaptureTap = Buttons.HasFlag(SwitchButtons.Capture) ? HostAction.GameBarScreenshot : HostAction.PassThrough,
        CaptureHold = Buttons.HasFlag(SwitchButtons.Capture) ? HostAction.GameBarToggleRecording : HostAction.PassThrough,
        HomeHold = Buttons.HasFlag(SwitchButtons.Home) ? HostAction.XboxGameBar : HostAction.PassThrough,
        ChatTap = Buttons.HasFlag(SwitchButtons.ChatC) ? HostAction.RecenterGyro : HostAction.PassThrough,
        ChatHold = HostAction.PassThrough,
        Magnetometer = NintendoIds.IsSwitch2Pid(ProductId) && ProductId != NintendoIds.NsoGameCubePid,
        AutoMergeJoyCons = PartnerId != 0 || !(NintendoIds.IsLeftJoyCon(ProductId) || NintendoIds.IsRightJoyCon(ProductId)),
        Rumble = 1f
    };

    public static IReadOnlyList<FactoryProfile> All { get; } =
    [
        new("pro2", NintendoIds.ProController2Pid, "Switch 2 Pro Controller"),
        new("joycon2-l", NintendoIds.JoyCon2LeftPid, "Joy-Con 2 sinistro"),
        new("joycon2-r", NintendoIds.JoyCon2RightPid, "Joy-Con 2 destro"),
        new("joycon2-pair", NintendoIds.JoyCon2LeftPid, "Coppia Joy-Con 2", NintendoIds.JoyCon2RightPid),
        new("pro", NintendoIds.ProController1Pid, "Switch Pro Controller"),
        new("joycon-l", NintendoIds.JoyCon1LeftPid, "Joy-Con sinistro"),
        new("joycon-r", NintendoIds.JoyCon1RightPid, "Joy-Con destro"),
        new("joycon-pair", NintendoIds.JoyCon1LeftPid, "Coppia Joy-Con", NintendoIds.JoyCon1RightPid),
        new("gamecube", NintendoIds.NsoGameCubePid, "GameCube per Switch 2")
    ];

    public static FactoryProfile? Find(ushort productId, ushort partnerId = 0) =>
        All.FirstOrDefault(model => model.ProductId == productId && model.PartnerId == partnerId);
}
