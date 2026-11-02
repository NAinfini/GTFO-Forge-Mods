namespace ForgeCore;

/// <summary>How alert an enemy is, coarsest to most: off (dead, no AI or switched off), asleep, awake without having noticed anyone, and hunting.
/// Libraries colour and filter by it; <c>Enemies.Alert</c> reads it from the game.</summary>
public enum EnemyAlert : byte { Off, Sleeping, Awake, Hunting }
