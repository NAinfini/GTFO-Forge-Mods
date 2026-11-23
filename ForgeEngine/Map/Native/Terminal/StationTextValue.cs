using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Localization;

namespace ForgeMap.Native;

/// <summary>Map's one reader of the player text language, for every surface that follows it (intel, objectives, success
/// screen, interaction prompts, placed models, labels, zone outlines). The Player session answers from a managed value that
/// moves only when the language does, so these readers may ask every frame.</summary>
internal sealed class MapTextLanguage
{
    internal bool TryRead(out Language language, out string code, out string detail)
    {
        language = default;
        code = PlayerTextLanguageService.MissingCode;
        detail = "The Player domain is not running.";
        if (ForgePlayer.Native.PlayerHost.Session?.TextLanguage is not { } provider) return false;
        try { language = provider.CurrentLanguage; code = detail = ""; return true; }
        catch (RuntimeContractException error) { code = error.Code; detail = error.Message; return false; }
        catch (System.Exception error)
        {
            MapLog.Contained("map-text-language.read", error);
            code = ResultCode.PlayerTextLanguageUnavailable; detail = error.GetType().Name;
            return false;
        }
    }
}

/// <summary>Station signs, counters and feedback carry a vanilla Text block id until this client's display.
/// The reference carries the author's primary language; no host language or invented translation is replicated.</summary>
internal static class StationTextValue
{
    internal static bool TryCurrentLanguage(out Language language, out string code)
    {
        if (MapHost.Session?.TextLanguage is { } reader) return reader.TryRead(out language, out code, out _);
        language = default; code = ResultCode.MapRuntimeHostMissing; return false;
    }
    internal static Language CurrentLanguage => LanguageFor(MapHost.Session?.Kernel);

    internal static Language LanguageFor(IForgeEngine? kernel)
    {
        if (kernel == null) throw new RuntimeContractException(ResultCode.MapRuntimeHostMissing,
            "The Map session host is unavailable while resolving the player text language.");
        if (MapHost.Session is { } session && ReferenceEquals(session.Kernel, kernel))
        {
            if (session.TextLanguage.TryRead(out var language, out var code, out var detail)) return language;
            throw new RuntimeContractException(code, detail);
        }
        return ForgePlayer.Native.PlayerHost.Session?.TextLanguage?.CurrentLanguage
            ?? throw new RuntimeContractException(PlayerTextLanguageService.MissingCode, "The Player domain is not running.");
    }

    internal static bool RequiresLanguage(string? literal) => ForgeUI.GameText.IsReference(literal);

    internal static bool TryRender(string literal, out string rendered)
        => TryRender(literal, MapHost.Session?.Kernel, out rendered);

    internal static bool TryRender(string literal, IForgeEngine? kernel, out string rendered)
    {
        if (!RequiresLanguage(literal))
        {
            rendered = literal;
            return true;
        }
        return TryRender(literal, LanguageFor(kernel), out rendered);
    }

    /// <summary>Renders one explicitly selected column, falling back only to the author's declared primary.
    /// Placed-model behavior uses this overload so a graph can choose a column without changing the client's
    /// effective language. The reading itself is the library's text service.</summary>
    internal static bool TryRender(string literal, Language language, out string rendered)
    {
        bool ok = ForgeEngine.Presentation.LevelText.TryRender(literal, language, out rendered, out var error);
        if (error != null) MapLog.Contained("station-text-value.try-render", error);
        return ok;
    }

    /// <summary>Reads a persisted optional text without turning an authored empty column into a missing resource.
    /// The caller can keep its owning object and hide only the label; an invalid reference or absent block still
    /// returns false and is reported by that owner.</summary>
    internal static bool TryRenderOptional(string literal, Language language, out string rendered, out bool empty)
    {
        bool ok = ForgeEngine.Presentation.LevelText.TryRenderOptional(literal, language, out rendered, out empty, out var error);
        if (error != null) MapLog.Contained("station-text-value.try-render", error);
        return ok;
    }
}
