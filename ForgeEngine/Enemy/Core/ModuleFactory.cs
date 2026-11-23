using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The one Enemy registration, used by the native session and the release exporter.</summary>
internal static partial class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.enemy";
    private static readonly ProviderDeclaration Declaration = EnemyRegistration.Declaration();
    public static ProviderDeclaration Describe() => Declaration;

    public static RuntimeModule Create(IReadOnlyDictionary<string, CommandHandler> handlers,
        IReadOnlyDictionary<string, EvaluatorHandler> evaluators, Func<EntityReference, bool> isCurrent) =>
        Declaration.Module(RuntimeApi.Fingerprint, handlers ?? throw new ArgumentNullException(nameof(handlers)),
            evaluators: evaluators ?? throw new ArgumentNullException(nameof(evaluators)),
            entityResolvers: new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [EntityKinds.Enemy] = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent))
            });
}
