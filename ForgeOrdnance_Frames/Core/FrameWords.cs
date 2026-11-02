using System;
using System.Globalization;

namespace ForgeOrdnanceFrames;

/// <summary>Every word the suit shows the player, read from this package's <c>lang/&lt;language&gt;.json</c> through ForgeUI (the native
/// side passes its text lookup; the offline tests pass the shipped files). Made again whenever the language changes; nothing here
/// allocates per frame. Texts with a <c>{0}</c> are formatted with <see cref="Format"/>.</summary>
public sealed class FrameWords
{
    private readonly Func<string, string> _text;

    public FrameWords(Func<string, string> text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        Barrier = text("hud.barrier"); Pilot = text("hud.pilot");
        Heat = text("hud.heat"); Overheat = text("hud.overheat"); Ready = text("hud.ready"); Active = text("hud.active"); On = text("hud.on");
        Off = text("hud.off"); Deploying = text("hud.deploying"); Rockets = text("hud.rockets"); Lamp = text("hud.lamp"); Exit = text("hud.exit"); CarryTake = text("hud.carryTake"); CarryStow = text("hud.carryStow");
        Charge = text("special.charge"); Siege = text("special.siege"); Scan = text("special.scan");
        BoardHeader = text("board.header"); BoardHold = text("board.hold"); BoardBehind = text("board.behind"); BoardArmor = text("board.armor");
        Failing = text("banner.failing"); Destroyed = text("banner.destroyed"); ForcedOut = text("banner.forcedOut");
        NotFree = text("refuse.notFree"); TooFar = text("refuse.tooFar"); AlreadyIn = text("refuse.alreadyIn"); PilotDown = text("refuse.pilotDown");
        NotReady = text("refuse.notReady"); Planted = text("refuse.planted"); Charging = text("refuse.charging"); RocketRange = text("refuse.range");
        NotNow = text("refuse.notNow");
        Link = text("boot.link"); BootSeal = text("boot.seal"); BootArmor = text("boot.armor"); BootServos = text("boot.servos");
        BootWeapon = text("boot.weapon"); BootSensors = text("boot.sensors"); LinkHeader = text("boot.header"); BootOk = text("boot.ok"); BootSync = text("boot.sync");
        Shutdown = text("boot.shutdown");
        Hull = text("hud.hull");
        LoadoutTitle = text("hud.loadout"); Squad = text("hud.squad"); Behind = text("hud.behind");
        Grabbed = text("hud.grabbed"); Stunned = text("hud.stunned");
        Piloting = text("slot.piloting");
    }

    /// <summary>Why nothing else can be lent into the special weapon slot while piloting (shown by other plugins, e.g. a heavy weapon rack).</summary>
    public string Piloting { get; }

    /// <summary>The banner while something grabs the pilot, and while the pilot is stunned.</summary>
    public string Grabbed { get; }
    public string Stunned { get; }

    /// <summary>A text with one <c>{0}</c>, filled with <paramref name="value"/>.</summary>
    public static string Format(string text, object value) => string.Format(CultureInfo.InvariantCulture, text, value);

    public string Barrier { get; }
    public string Pilot { get; }
    public string Heat { get; }
    public string Overheat { get; }
    public string Ready { get; }
    public string Active { get; }
    public string On { get; }
    public string Off { get; }
    public string Deploying { get; }
    public string Rockets { get; }
    public string Lamp { get; }
    public string Exit { get; }
    public string CarryTake { get; }
    public string CarryStow { get; }
    public string Charge { get; }
    public string Siege { get; }
    public string Scan { get; }
    public string BoardHeader { get; }
    /// <summary>"Hold {0} to board".</summary>
    public string BoardHold { get; }
    public string BoardBehind { get; }
    /// <summary>"Armor {0}%".</summary>
    public string BoardArmor { get; }
    public string Failing { get; }
    public string Destroyed { get; }
    public string ForcedOut { get; }
    public string NotFree { get; }
    public string TooFar { get; }
    public string AlreadyIn { get; }
    public string PilotDown { get; }
    public string NotReady { get; }
    public string Planted { get; }
    public string Charging { get; }
    public string RocketRange { get; }
    public string NotNow { get; }
    /// <summary>The power-on's big message (NEURAL LINK OK) and the bar's header over it (NEURAL LINK).</summary>
    public string Link { get; }
    public string LinkHeader { get; }
    /// <summary>The power-down word in the middle of the screen while leaving (SHUTDOWN).</summary>
    public string Shutdown { get; }
    /// <summary>The self-test's stamp (OK) and the bar's stall tag (SYNC).</summary>
    public string BootOk { get; }
    public string BootSync { get; }
    public string BootSeal { get; }
    /// <summary>"ARMOR {0}%".</summary>
    public string BootArmor { get; }
    public string BootServos { get; }
    public string BootWeapon { get; }
    public string BootSensors { get; }
    /// <summary>The damage disc's caption (HULL).</summary>
    public string Hull { get; }
    /// <summary>The inventory panel's title (LOADOUT).</summary>
    public string LoadoutTitle { get; }
    /// <summary>The teammates' header.</summary>
    public string Squad { get; }
    /// <summary>"BEHIND ×{0}": hunting contacts behind the pilot.</summary>
    public string Behind { get; }
}
