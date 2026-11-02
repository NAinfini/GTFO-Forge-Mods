namespace ForgeCore.Tests;

/// <summary><c>GameEvents</c>: a change is raised once, a handler that throws does not stop the others, and the language and
/// player-down entries are asked for only when something needs them.</summary>
internal static class EventChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        GameEvents.Reset();
        var languageNeeded = 0; var downNeeded = 0;
        GameEvents.NeedLanguage = () => languageNeeded++;
        GameEvents.NeedLocalPlayerDown = () => downNeeded++;

        var masters = new List<ulong>();
        GameEvents.MasterChanged += () => throw new InvalidOperationException("broken handler");
        GameEvents.MasterChanged += () => masters.Add(GameEvents.Master);
        var errorsBefore = errors.Count;
        GameEvents.NoteMaster(7);
        GameEvents.NoteMaster(7);
        GameEvents.NoteMaster(0);
        check(masters.SequenceEqual(new ulong[] { 7, 0 }), "events: the host change is raised once per change, with Master already updated");
        check(errors.Count == errorsBefore + 1, "events: a throwing handler is logged once and the next handler still runs");

        check(languageNeeded == 0 && downNeeded == 0, "events: nothing is patched before a language or player-down reader exists");
        var languages = 0;
        GameEvents.LanguageChanged += () => languages++;
        var revision = GameEvents.LanguageRevision;
        GameEvents.NoteLanguage();
        check(languageNeeded >= 1 && languages == 1 && GameEvents.LanguageRevision == revision + 1,
            "events: subscribing to the language asks for its entry; a set raises the revision before the event");

        var downs = 0;
        GameEvents.LocalPlayerDown += () => downs++;
        GameEvents.NoteLocalPlayerDown();
        check(downNeeded == 1 && downs == 1, "events: subscribing to the local player's death asks for its entry once per subscription");

        var ended = 0; var reloaded = 0; var reached = 0;
        GameEvents.LevelEnded += () => ended++;
        GameEvents.CheckpointReloaded += () => reloaded++;
        GameEvents.CheckpointReached += () => reached++;
        GameEvents.NoteLevelEnded();
        GameEvents.NoteCheckpointReached();
        GameEvents.NoteCheckpointReloaded();
        check(ended == 1 && reloaded == 1 && reached == 1, "events: level end, checkpoint and checkpoint reload reach their subscribers");

        var lobbies = new List<bool>(); var selections = new List<string>(); var selectionNeeded = 0;
        GameEvents.NeedExpeditionSelected = () => selectionNeeded++;
        GameEvents.SeedLobby(true);
        GameEvents.LobbyChanged += lobbies.Add;
        GameEvents.NoteLobby(true);
        GameEvents.NoteLobby(false);
        GameEvents.NoteLobby(true);
        check(lobbies.SequenceEqual(new[] { false, true }) && GameEvents.InLobby,
            "events: a seeded lobby state raises nothing; leaving and entering the lobby raise once each, with InLobby already updated");
        check(selectionNeeded == 0, "events: the expedition-selection entry is not patched before something subscribes");
        GameEvents.ExpeditionSelected += (rundown, tier, index) => selections.Add(rundown + "/" + tier + "/" + index);
        GameEvents.ExpeditionSelected += (_, _, _) => throw new InvalidOperationException("broken handler");
        GameEvents.NoteExpeditionSelected(42, 1, 0);
        GameEvents.NoteExpeditionSelected(null, 2, 3);
        check(selectionNeeded == 2 && selections.SequenceEqual(new[] { "42/1/0", "/2/3" }),
            "events: subscribing to the expedition choice asks for its entry; the Local rundown id (null for another key), tier and index reach the handler");

        GameEvents.Reset();
        GameEvents.NeedExpeditionSelected = static () => { };
        GameEvents.NeedLanguage = static () => { };
        GameEvents.NeedLocalPlayerDown = static () => { };
    }
}
