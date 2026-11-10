using ForgeCore;
using ForgeEmote;

// The public definition-file contract: a good file reads, a bad one is refused with the path of the bad field.
var failures = 0;
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (condition) return;
    failures++;
    Console.WriteLine("FAIL " + name);
}

string? Refused(string json)
{
    try
    {
        EmoteFile.Parse(json, "");
        return null;
    }
    catch (StrictJsonException error)
    {
        return error.Field;
    }
}

const string Good = """[{"Id":"pack:wave","Name":"wave","Page":"gesture","Slot":2,"Clip":"Clips/wave.clip.forge"}]""";
var read = EmoteFile.Parse(Good, "folder");
Check(read.Count == 1 && read[0].Id == "pack:wave" && read[0].Slot == 2 && read[0].Folder == "folder", "a valid file is read");
Check(Refused(Good) == null, "a valid file is not refused");
Check(Refused("""[{"Id":"pack:wave","Name":"wave","Page":"gesture","Clip":"c","Surprise":1}]""") == "[0].Surprise", "an unknown field is refused with its path");
Check(Refused("""[{"Id":"pack:wave","Name":"wave","Page":"gesture","Clip":"c","Body":"legs"}]""") == "[0].Body", "a Body outside the list is refused");
Check(Refused("""[{"Id":"wave","Name":"wave","Page":"gesture","Clip":"c"}]""") == "[0].Id", "an Id without a colon is refused");
Check(Refused("""[{"Id":"pack:","Name":"wave","Page":"gesture","Clip":"c"}]""") == "[0].Id", "an Id with nothing after the colon is refused");

// The same checks apply to a definition given in code (Emotes.Register calls EmoteRules.Validate).
Check(EmoteRules.Validate(read[0]) == null, "a valid definition passes the shared check");
Check(EmoteRules.Validate(new ForgeEmote.Api.EmoteDefinition { Id = "pack:x", Name = "x", Page = "gesture", Clip = "c", Body = "legs" }) is { Field: "Body" }, "a code definition with a Body outside the list is refused");
Check(EmoteRules.Validate(new ForgeEmote.Api.EmoteDefinition { Id = "x", Name = "x", Page = "gesture", Clip = "c" }) is { Field: "Id" } && EmoteRules.Validate(new ForgeEmote.Api.EmoteDefinition { Id = "pack:x", Name = "x", Page = "gesture", Clip = "c", Slot = 8 }) is { Field: "Slot" }, "a code definition with an Id without a colon or a Slot out of range is refused");

// The two-player flow: the offerer waits with the hand out; a teammate beyond reach or in another dimension does not pair it; the answerer waits for the start; both sides give up when nothing comes.
var link = new EmotePair();
link.Wait(10f);
Check(!link.Pair(7, 2f, true) && !link.Pair(7, 1f, false) && link.Now == EmotePair.Phase.Offering, "a teammate beyond 1.5 m or in another dimension does not pair");
Check(!link.Expired(10f + EmotePair.WaitSeconds - 0.1f) && link.Expired(10f + EmotePair.WaitSeconds), "an unanswered offer expires after the wait");
Check(link.Pair(7, EmotePair.Range + EmotePair.Slack, true) && link.Now == EmotePair.Phase.Paired && link.Partner == 7 && !link.Expired(999f), "a teammate the answerer judged within 1.5 m pairs (the offerer allows the network slack), and a pair does not expire");
var answerer = new EmotePair();
answerer.Accept(3, 20f);
Check(answerer.Now == EmotePair.Phase.Accepting && answerer.Partner == 3 && !answerer.Pair(3, 1f, true) && !answerer.Expired(20f + EmotePair.AcceptSeconds - 0.1f) && answerer.Expired(20f + EmotePair.AcceptSeconds), "an answer waits a short while for the offerer to start, and gives up when it does not");
answerer.Seal();
Check(answerer.Now == EmotePair.Phase.Paired && !answerer.Expired(999f), "the answerer takes the offerer's start");
// One clip time for both players from the same start: the hands stay out while offered, the clasp is held and moves a little, then the clip goes on; both ends meet the clip's own poses.
const float Hold = 2f;
Check(EmotePair.ClipTime(EmotePair.Phase.Offering, 123f, Hold) == Hold && EmotePair.ClipTime(EmotePair.Phase.Accepting, 0f, Hold) == Hold, "an offered hand stays at the hands-out moment");
Check(EmotePair.ClipTime(EmotePair.Phase.Paired, Hold, Hold) == Hold && Math.Abs(EmotePair.ClipTime(EmotePair.Phase.Paired, Hold + EmotePair.ClaspSeconds, Hold) - Hold) < 1e-4f
    && Math.Abs(EmotePair.ClipTime(EmotePair.Phase.Paired, Hold + EmotePair.ClaspSeconds + 0.5f, Hold) - (Hold + 0.5f)) < 1e-4f, "the clasp is held, then the clip goes on from the same pose");
Check(EmotePair.ClipTime(EmotePair.Phase.Paired, Hold + 0.25f, Hold) is > Hold and <= Hold + EmotePair.WobbleSpan, "the held hands move within the clip's own poses");
link.Clear();
var stars = new List<string> { "gone:1", "gone:2", "a", "b", "c", "d", "e", "f" };
Func<string, bool> here = id => !id.StartsWith("gone:");
Check(EmoteRules.Toggle(stars, "g", here) && EmoteRules.Toggle(stars, "h", here) && stars.Count == 10 && !EmoteRules.Toggle(stars, "i", here) && stars.Contains("gone:1"), "stars of what is no longer installed take no place and are kept");
Check(link.Now == EmotePair.Phase.None && ReadPair("""[{"Id":"pack:five","Name":"five","Page":"gesture","Clip":"c","Pair":true}]"""), "Pair is read from a definition file");
static bool ReadPair(string json) => EmoteFile.Parse(json, "")[0].Pair;

Console.WriteLine(failures == 0 ? "ForgeEmote core: " + checks + " checks passed, 0 failed" : failures + " check(s) failed");
return failures == 0 ? 0 : 1;
