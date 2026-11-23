namespace ForgeEngine;

/// <summary>Configured startup mode, not a live permission or authority grant. Play and Authoring run the same host;
/// only Authoring lets the optional Development plugin start diagnostics.</summary>
public enum RuntimeMode { Off = 0, Authoring = 1, Play = 2 }
