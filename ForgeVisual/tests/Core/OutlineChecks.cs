using EnemyAlert = ForgeCore.EnemyAlert;
using ForgeVisual;

/// <summary>The outline's pure rules: style checks, the automatic enemy selection, the claims on single objects and their priority,
/// the switch scopes, the draw set, and that the shipped bundle holds the current outline shader with its X-ray pass.</summary>
internal static class OutlineChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // Width follows the screen height from 1080p.
        check(OutlineRules.PixelWidth(2f, 1080) == 2f, "outline: 1080p width");
        check(OutlineRules.PixelWidth(2f, 2160) == 4f, "outline: 4K width doubles");
        check(OutlineRules.PixelWidth(2f, 0) > 0f, "outline: zero height still positive");

        // Defaults: the cockpit look is unchanged (asleep fainter than awake, awake fainter than hunting; 1.6 px; 35 m; 24 enemies; no X-ray).
        var style = new EnemyOutlineStyle();
        check(style.Problem() == null, "outline: default style valid");
        check(style.Width == 1.6f && style.Range == 35f && style.FadeDistance == 5f && style.MaxEnemies == 24 && !style.XRay && style.Priority == 0,
            "outline: default width, range, fade, count, depth test and priority");
        check(style.Sleeping.Equals(new OutlineColor(0.23f, 0.40f, 0.44f, 0.40f)) && style.Awake.Equals(new OutlineColor(0.80f, 0.56f, 0.22f, 0.65f))
            && style.Hunting.Equals(new OutlineColor(0.80f, 0.18f, 0.18f, 0.80f)), "outline: default state colours");
        check(style.ColorOf(EnemyAlert.Hunting).Equals(style.Hunting) && style.ColorOf(EnemyAlert.Off).A == 0f, "outline: colour by state");
        check(new EnemyOutlineStyle { Range = 0f }.Problem() != null, "outline: zero range refused");
        check(new EnemyOutlineStyle { Range = float.NaN }.Problem() != null, "outline: NaN range refused");
        check(new EnemyOutlineStyle { FadeDistance = 50f }.Problem() != null, "outline: fade longer than range refused");
        check(new EnemyOutlineStyle { MaxEnemies = 0 }.Problem() != null && new EnemyOutlineStyle { MaxEnemies = 129 }.Problem() != null, "outline: count limits");
        check(new EnemyOutlineStyle { Width = 0f }.Problem() != null, "outline: zero width refused");
        check(new EnemyOutlineStyle { Hunting = new OutlineColor(1f, 0f, 0f, 2f) }.Problem() != null, "outline: alpha above 1 refused");
        check(new EnemyOutlineStyle { Hunting = new OutlineColor(3f, 0f, 0f, 1f) }.Problem() == null, "outline: HDR colour accepted");
        check(OutlineRules.Problem(new OutlineColor(1f, 1f, 1f), 0f) == null && OutlineRules.Problem(new OutlineColor(1f, 1f, 1f), -1f) != null
            && OutlineRules.Problem(new OutlineColor(float.NaN, 0f, 0f), 1f) != null, "outline: added outline values checked");
        check(float.IsPositiveInfinity(OutlineRules.Until(5f, 0f)) && OutlineRules.Until(5f, 2f) == 7f, "outline: 0 seconds lasts until removed");

        // Wake window: renderers are read again every quarter second inside it, never before the next scan, never after it.
        check(OutlineRules.WakeScanDue(1f, 7f, 1f) && !OutlineRules.WakeScanDue(1f, 7f, 1.1f) && !OutlineRules.WakeScanDue(7f, 7f, 0f),
            "outline: wake window scans on schedule and stops at its end");

        // Selection: camera at the origin looking along +z. Slots and generations come from ForgeCore's registry; a state of None is its Off.
        static bool Select(EnemyOutlineTracker t, EnemyOutlineStyle s) => t.Select(s, 0f, 0f, 0f, 0f, 0f, 1f);
        static int[] Selected(EnemyOutlineTracker t) => Enumerable.Range(0, t.SelectedCount).Select(t.SelectedSlot).ToArray();

        var tracker = new EnemyOutlineTracker();
        const int ahead = 0, far = 1, behind = 2, beside = 3, unset = 4;
        tracker.Set(ahead, 1, 0f, 0f, 10f, EnemyAlert.Sleeping);
        tracker.Set(far, 1, 0f, 0f, 40f, EnemyAlert.Hunting);
        tracker.Set(behind, 1, 0f, 0f, -10f, EnemyAlert.Hunting);
        tracker.Set(beside, 1, 0f, 0f, -2f, EnemyAlert.Awake);
        tracker.Set(unset, 1, 0f, 0f, 5f, EnemyAlert.Off);
        check(Select(tracker, style), "outline: first selection is a change");
        check(Selected(tracker).SequenceEqual(new[] { ahead, beside }), "outline: in range and in view, plus the one right beside; not far, behind or off");
        check(!Select(tracker, style), "outline: same picture is no change");
        tracker.Set(ahead, 1, 0f, 0f, 11f, EnemyAlert.Sleeping);
        check(!Select(tracker, style), "outline: moving inside range is no change");
        tracker.Set(ahead, 1, 0f, 0f, 11f, EnemyAlert.Hunting);
        check(Select(tracker, style) && tracker.SelectedState(0) == EnemyAlert.Hunting, "outline: waking up is a change");
        tracker.Set(far, 1, 0f, 0f, 30f, EnemyAlert.Hunting);
        check(Select(tracker, style) && Selected(tracker).Contains(far), "outline: coming into range is a change");
        check(!Select(tracker, new EnemyOutlineStyle { Range = 35f }), "outline: equal style, no change");
        check(Select(tracker, new EnemyOutlineStyle { Range = 20f }) && !Selected(tracker).Contains(far), "outline: shorter range drops the far one");
        tracker.Set(unset, 1, 0f, 0f, 5f, EnemyAlert.Awake);
        check(Select(tracker, new EnemyOutlineStyle { Range = 20f }) && Selected(tracker).Contains(unset), "outline: an enemy no longer off is picked up");

        // A wide view cone: slightly off to the side and past 90 degrees by a little still counts.
        var cone = new EnemyOutlineTracker();
        cone.Set(0, 1, 10f, 0f, -0.5f, EnemyAlert.Awake);
        check(Select(cone, style) && cone.SelectedCount == 1, "outline: just past the side is in the cone");
        cone.Set(0, 1, 10f, 0f, -5f, EnemyAlert.Awake);
        check(Select(cone, style) && cone.SelectedCount == 0, "outline: well behind is out");

        // Nearest first when more than MaxEnemies.
        var many = new EnemyOutlineTracker();
        for (int i = 0; i < 10; i++) many.Set(i, 1, 0f, 0f, 30f - i * 2f, EnemyAlert.Awake);
        Select(many, new EnemyOutlineStyle { MaxEnemies = 3 });
        check(Selected(many).SequenceEqual(new[] { 7, 8, 9 }), "outline: the three nearest kept");

        // A slot the registry vacates is released (the caller must then stop drawing it at once); a reused slot never matches the old selection.
        var reuse = new EnemyOutlineTracker();
        reuse.Set(0, 1, 0f, 0f, 5f, EnemyAlert.Awake);
        Select(reuse, style);
        check(reuse.Release(0) && !reuse.Used(0), "outline: releasing a selected enemy says so");
        check(!reuse.Release(0), "outline: releasing twice does nothing");
        reuse.Set(0, 2, 0f, 0f, 5f, EnemyAlert.Awake);
        check(Select(reuse, style) && reuse.Generation(0) == 2, "outline: a new enemy in a reused slot is a change");
        reuse.Set(0, 3, 0f, 0f, 5f, EnemyAlert.Awake);
        check(Select(reuse, style), "outline: a replaced enemy in the same slot, same place and state, is a change");
        check(!reuse.Release(2) && !reuse.Release(-1) && !reuse.Release(99), "outline: releasing a slot never set does nothing");
        reuse.ClearSelection();
        check(reuse.SelectedCount == 0 && Select(reuse, style), "outline: cleared selection reports a change next time");

        // The cursor goes round every slot in turn.
        var robin = new EnemyOutlineTracker();
        check(Enumerable.Range(0, 7).Select(_ => robin.NextSlot(3)).SequenceEqual(new[] { 0, 1, 2, 0, 1, 2, 0 }), "outline: cursor goes round the slots");
        check(robin.NextSlot(2) == 1 && robin.NextSlot(2) == 0, "outline: cursor wraps when the slot count shrinks");
        check(new EnemyOutlineTracker().NextSlot(0) == -1, "outline: no slots, nothing to read");

        // Growth beyond the first arrays.
        var big = new EnemyOutlineTracker();
        for (int i = 0; i < 200; i++) big.Set(i, 1, 0f, 0f, 1f + i * 0.1f, EnemyAlert.Awake);
        Select(big, new EnemyOutlineStyle { MaxEnemies = 128 });
        check(big.Capacity == 200 && big.SelectedCount == 128, "outline: 200 slots set, 128 selected");

        // Claims on single objects: one drawn per object, the highest priority, the latest among equals; time and handles.
        var claims = new OutlineClaims();
        var low = claims.Add(100, 0, float.PositiveInfinity);
        var high = claims.Add(100, 5, float.PositiveInfinity);
        var later = claims.Add(100, 5, float.PositiveInfinity);
        var other = claims.Add(200, -3, 10f);
        check(claims.Winner(100) == later && claims.Winner(200) == other && claims.Winner(300) == -1, "outline: highest priority wins, latest among equals");
        check(claims.Remove(later, claims.Generation(later)) && claims.Winner(100) == high, "outline: removing the winner falls back to the next");
        check(!claims.Remove(later, claims.Generation(later) - 1), "outline: an old handle removes nothing");
        var reused = claims.Add(100, 1, float.PositiveInfinity);
        check(reused == later && !claims.Remove(reused, claims.Generation(reused) - 1) && claims.Live(reused), "outline: a reused slot is not removed by the old handle");
        check(claims.Overrides(100, 5) && !claims.Overrides(100, 6) && !claims.Overrides(300, int.MinValue), "outline: an added outline of equal or higher priority replaces the automatic one");
        check(!claims.Expire(9.9f) && claims.Expire(10f) && !claims.Live(other) && claims.Winner(200) == -1, "outline: a timed outline ends on time");
        claims.Clear();
        check(claims.Count == 0 && claims.Winner(100) == -1 && !claims.Live(low), "outline: clear ends every outline");
        for (int i = 0; i < 40; i++) claims.Add(i, 0, float.PositiveInfinity);
        check(claims.Count == 40 && claims.Capacity == 40, "outline: claims grow past their first arrays");

        // Every-frame work does not allocate once warmed up.
        var hot = new EnemyOutlineTracker();
        for (int i = 0; i < 100; i++) hot.Set(i, 1, i % 7, 0f, i * 0.3f, (EnemyAlert)(1 + i % 3));
        var hotStyle = new EnemyOutlineStyle();
        for (int f = 0; f < 10; f++) { for (int i = 0; i < OutlineRules.RefreshPerFrame; i++) hot.NextSlot(100); Select(hot, hotStyle); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 1000; f++)
        {
            for (int i = 0; i < OutlineRules.RefreshPerFrame; i++)
            {
                int s = hot.NextSlot(100);
                hot.Set(s, 1 + f / 100, f % 5, 0f, (s + f) % 40, (EnemyAlert)(1 + (f + i) % 3));
            }
            hot.Select(hotStyle, 0f, 0f, 0f, 0f, 0f, 1f);
            claims.Expire(f);
            claims.Winner(f % 40);
            claims.Overrides(f % 40, 0);
        }
        check(GC.GetAllocatedBytesForCurrentThread() == before, "outline: per-frame refresh, selection and claims allocate nothing");

        // Switch scopes: drawn while any is on; the last switched on wins; switching it off falls back to the other.
        var scopes = new OutlineScopes<string>();
        string frames = "frames", other2 = "other";
        check(scopes.Current == null, "outline: nothing on");
        check(scopes.Set(frames, true) && scopes.Current == frames, "outline: frames on");
        check(!scopes.Set(frames, true), "outline: on twice is no change");
        check(scopes.Set(other2, true) && scopes.Current == other2, "outline: later scope wins");
        check(scopes.Set(other2, false) && scopes.Current == frames, "outline: back to the earlier one");
        check(scopes.Set(frames, false) && scopes.Current == null && !scopes.IsOn(frames), "outline: all off");

        // Shot-off limbs: a renderer the game no longer draws leaves the draw set; the others stay; the set is capped.
        var head = new Part(true); var arm = new Part(true); var body = new Part(true);
        var parts = new List<Part> { head, arm, body };
        var drawSet = new List<Part>();
        check(OutlineDrawSet.Collect(parts, p => p.Enabled, 16, drawSet) == 0 && drawSet.Count == 3, "outline: all drawn parts are in the set");
        head.Enabled = false;
        check(OutlineDrawSet.Collect(parts, p => p.Enabled, 16, drawSet) == 1 && !drawSet.Contains(head) && drawSet.Count == 2, "outline: the destroyed head leaves the set");
        OutlineDrawSet.Collect(Enumerable.Range(0, 20).Select(_ => new Part(true)).ToList(), p => p.Enabled, 16, drawSet);
        check(drawSet.Count == 16, "outline: the set is capped");

        // The shipped bundle is built from the current shaders, and the outline draws only the outer silhouette: a pixel inside any fill
        // draws nothing, so interior edges never outline. The X-ray fill is the same fill without the depth test.
        var library = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
        var tools = Path.Combine(library, "tools", "shaders");
        var all = string.Concat(new[] { "ForgeVisualParticle.shader", "ForgeVisualMarker.shader", "ForgeVisualOutline.shader" }
            .Select(name => File.ReadAllText(Path.Combine(tools, name)).Replace("\r\n", "\n")));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all))).ToLowerInvariant();
        var generated = File.ReadAllText(Path.Combine(library, "Native", "ShaderBundle.Generated.cs"));
        check(generated.Contains("SourceSha256 = \"" + sha + "\"") && generated.Contains("OutlineAsset = \"Assets/ForgeVisualOutline.shader\""),
            "outline: bundle built from the current shaders, outline included");
        var shader = File.ReadAllText(Path.Combine(tools, "ForgeVisualOutline.shader")).Replace("\r\n", "\n");
        var passes = shader.Split("Pass\n");
        check(passes.Length == 4 && passes[1].Contains("Name \"Fill\"") && passes[2].Contains("Name \"Composite\"") && passes[3].Contains("Name \"FillXRay\""),
            "outline: passes in order fill, composite, X-ray fill");
        check(passes[1].Contains("ZTest LEqual") && passes[3].Contains("ZTest Always") && passes[3].Contains("vert_fill") && passes[3].Contains("frag_fill"),
            "outline: the X-ray fill is the fill without the depth test");
        check(shader.Contains("Cull Back") && !shader.Contains("Cull Front"), "outline: fill draws front faces, no back-face extrusion");
        check(shader.Contains("if (tex2D(_MainTex, i.uv).a > 0.5) return float4(0, 0, 0, 0);"), "outline: inside the silhouette draws nothing");
        check(shader.Contains("clip(0.5 - (_EllipsoidInverts[k] != 0.0 ? d : 1.0 - d));") && shader.Contains("mul(_EllipsoidMtx[k], float4(i.rest, 1.0))")
            && shader.Contains("float4 color : COLOR;") && shader.Contains("o.rest = v.color.xyz;"),
            "outline: fill cuts by the rest position in the vertex colour, as the game's enemy shaders do");

        // Shot-off limbs, the game's cut. Rest-pose positions read from the game's own meshes (vertex colour = position): a Striker head
        // vertex (g_head_shot_CB; the Giant is this body scaled up) and a Tank head vertex (g_tank_CB). Each body is cut by a sphere of
        // radius 0.25 round its head, built the way the game builds its ellipsoids (rest mesh space into a unit space, cut inside 0.5).
        float[] Sphere(float cx, float cy, float cz, float r)
        {
            float s = 0.5f / r;
            return new[] { s, 0, 0, -s * cx, 0, s, 0, -s * cy, 0, 0, s, -s * cz, 0, 0, 0, 1 };
        }
        var none = new float[OutlineCuts.Max];
        var strikerHead = Sphere(-0.0897f, 1.5789f, 0.0165f, 0.25f);
        check(OutlineCuts.Removes(strikerHead, none, 1, -0.0897f, 1.5789f, 0.0165f), "outline cut: the shot-off Striker / Giant head is dropped");
        check(!OutlineCuts.Removes(strikerHead, none, 1, -0.1441f, 1.1489f, -0.0520f), "outline cut: the Striker's hip stays");
        check(!OutlineCuts.Removes(strikerHead, none, 0, -0.0897f, 1.5789f, 0.0165f), "outline cut: no cut while the game has none");
        // The earlier fill tested the world position: the same head standing at (35, -4, 120) was never cut, so the line kept the head.
        check(!OutlineCuts.Removes(strikerHead, none, 1, 35f - 0.0897f, -4f + 1.5789f, 120f + 0.0165f),
            "outline cut: a world position misses the rest-space ellipsoid (the old bug)");
        var tankHead = Sphere(0.2466f, 1.7339f, -0.0427f, 0.25f);
        check(OutlineCuts.Removes(tankHead, none, 1, 0.2466f, 1.7339f, -0.0427f), "outline cut: the Tank's cut part is dropped");
        check(!OutlineCuts.Removes(tankHead, none, 1, 0.1606f, 0.3703f, -0.0551f), "outline cut: the Tank's leg stays");
        // Two cuts (head and left arm, as in the owner's screenshot): either drops; inverted keeps only the inside.
        var two = strikerHead.Concat(Sphere(-0.4182f, 1.2267f, -0.0358f, 0.2f)).ToArray();
        check(OutlineCuts.Removes(two, none, 2, -0.4182f, 1.2267f, -0.0358f) && OutlineCuts.Removes(two, none, 2, -0.0897f, 1.5789f, 0.0165f)
            && !OutlineCuts.Removes(two, none, 2, 0.0067f, 0.9660f, 0.0425f), "outline cut: head and arm both dropped, belly stays");
        var inverted = new float[] { 1, 0, 0, 0 };
        check(!OutlineCuts.Removes(strikerHead, inverted, 1, -0.0897f, 1.5789f, 0.0165f) && OutlineCuts.Removes(strikerHead, inverted, 1, -0.1441f, 1.1489f, -0.0520f),
            "outline cut: an inverted ellipsoid cuts everything outside it");
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private sealed class Part(bool enabled)
    {
        public bool Enabled { get; set; } = enabled;
    }
}
