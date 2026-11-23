using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>A resolved native modifier and its signed additive contribution; subtract has already changed its sign.</summary>
internal readonly record struct EnemyAttributeValue(int Modifier, float Amount);

/// <summary>Amount is the native table's observed contribution for the first member, never a returned native id.</summary>
internal readonly record struct EnemyAttributeReceipt(Status Status, Commit Commit, string Code, double Amount);
