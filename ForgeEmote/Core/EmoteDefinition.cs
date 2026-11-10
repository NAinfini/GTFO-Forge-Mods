namespace ForgeEmote.Api;

/// <summary>Where the player watches an action from.</summary>
public enum EmoteView
{
    /// <summary>From the player's own eyes.</summary>
    FirstPerson,
    /// <summary>From behind the player.</summary>
    ThirdPerson,
}

/// <summary>Whether the player keeps moving during an action.</summary>
public enum EmoteMove
{
    /// <summary>Walking, running and shooting carry on.</summary>
    Free,
    /// <summary>Stands still; moving ends the action.</summary>
    Locked,
}

/// <summary>One action of the wheel, as read from a <c>*.emote.forge</c> or given to <see cref="Emotes.Register"/>.</summary>
public sealed class EmoteDefinition
{
    /// <summary><c>pack:entry</c>; never changes once published.</summary>
    public string Id { get; init; } = "";
    /// <summary>Language key of the name, in the <c>lang</c> folder beside the definition.</summary>
    public string Name { get; init; } = "";
    /// <summary>ForgeUI icon name, <c>pack:entry</c> (<c>UIKitMod.Icons</c>); optional.</summary>
    public string? Icon { get; init; }
    /// <summary><c>tactical</c>, <c>gesture</c>, <c>dance</c> or a page of your own.</summary>
    public string Page { get; init; } = "";
    /// <summary>Default place on its page, from 0.</summary>
    public int Slot { get; init; }
    /// <summary>Clip file beside the definition, played on the body.</summary>
    public string Clip { get; init; } = "";
    /// <summary>Clip made for the first-person view, shown when the player watches from inside; without one no arms show.</summary>
    public string? FirstPersonClip { get; init; }
    /// <summary><c>left-arm</c>, <c>right-arm</c>, <c>arms</c>, <c>upper</c> or <c>full</c>.</summary>
    public string Body { get; init; } = "upper";
    /// <summary>Repeats until cancelled.</summary>
    public bool Loop { get; init; }
    /// <summary>Seconds from the game's pose to the clip's.</summary>
    public float FadeIn { get; init; } = 0.15f;
    /// <summary>Seconds back to the game's pose.</summary>
    public float FadeOut { get; init; } = 0.2f;
    /// <summary>Where the player who does it watches from.</summary>
    public EmoteView View { get; init; }
    /// <summary>Whether the player can move during it.</summary>
    public EmoteMove Move { get; init; }
    /// <summary>The held item is out of the picture during the action.</summary>
    public bool HideHeld { get; init; }
    /// <summary>Taking damage ends the action.</summary>
    public bool CancelOnHit { get; init; }
    /// <summary>A rude gesture: a viewer can choose not to see it.</summary>
    public bool Crude { get; init; }
    /// <summary>Text ID of the game's own communication line said when it starts; 0 for none.</summary>
    public uint Comms { get; init; }
    /// <summary>Places the game's player marker at the crosshair when it starts.</summary>
    public bool Ping { get; init; }
    /// <summary>ForgeAudio cue in the <c>audio</c> folder beside the definition, heard from the body from the action's start (a looping cue for a looping action); optional.</summary>
    public string? Sound { get; init; }
    /// <summary>A two-player action: the player's hand goes out and stays out while a teammate within 1.5 m answers by interacting (or by picking the same action on their wheel; its slot glows meanwhile);
    /// both then step to arm's length, face each other and play the rest of the clip together. With no answer it cancels itself after a while, and moving or any action key cancels it earlier.</summary>
    public bool Pair { get; init; }
    /// <summary>For a <see cref="Pair"/> action: seconds into the clip at which the hands are out, where the clip waits for the other player and where the clasp is held.</summary>
    public float PairHold { get; init; }
    /// <summary>The folder the files are relative to.</summary>
    public string Folder { get; init; } = "";
}
