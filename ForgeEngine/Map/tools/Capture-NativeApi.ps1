# Read metadata only. Never load or invoke a GTFO type; only Mono.Cecil executes.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BepInExRoot,
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [Parameter(Mandatory=$true)][string]$OutFile,
    [string]$TargetsFile = (Join-Path $PSScriptRoot 'native-api-targets.json')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $OutFile) { throw 'Output already exists; use a new capture path.' }
$targets = @(Get-Content -LiteralPath $TargetsFile -Raw -Encoding utf8 | ConvertFrom-Json)
if (!$targets.Count) { throw 'No audit targets supplied.' }
if (@($targets.type | Select-Object -Unique).Count -ne $targets.Count) { throw 'Duplicate audit type.' }
. (Join-Path $PSScriptRoot '../../../Testing/SourceTools/InteropSignature.ps1') -BepInEx $BepInExRoot
function Get-Sha([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-AllTypes($Items) {
    foreach ($item in $Items) {
        $item
        if ($item.HasNestedTypes) { Get-AllTypes $item.NestedTypes }
    }
}
$assemblies = @(); $types = @(); $missing = @()
# The assemblies the audit resolves a target against. `Utils-ASM` is here for one member: the clock the objective
# machine's countdown is taken against, `Clock.ExpeditionProgressionTime`, which the game declares in no namespace
# and that assembly carries. A target that names a type no assembly here holds is reported in `missing` rather
# than resolved from somewhere else.
$files = @('interop/Modules-ASM.dll','interop/GameData-ASM.dll','interop/SNet_ASM.dll',
    'interop/Utils-ASM.dll',
    'interop/UnityEngine.CoreModule.dll','interop/UnityEngine.AIModule.dll',
    'core/BepInEx.Core.dll','core/BepInEx.Unity.IL2CPP.dll',
    'core/Il2CppInterop.Runtime.dll','core/Mono.Cecil.dll')
foreach ($relative in $files) {
    $path = Join-Path $BepInExRoot $relative
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        # An interop assembly is generated per profile: only its signature names the game. core/ holds shipped binaries.
        if ($relative -like 'interop/*') {
            $assemblies += [ordered]@{ file=$relative; signatureSha256=[ForgeTestInputs.InteropSignature]::Sha256($assembly).ToLowerInvariant();
                name=$assembly.Name.Name; version=$assembly.Name.Version.ToString() }
        } else {
            $assemblies += [ordered]@{ file=$relative; sha256=(Get-Sha $path);
                name=$assembly.Name.Name; version=$assembly.Name.Version.ToString();
                mvid=$assembly.MainModule.Mvid.ToString() }
        }
        foreach ($type in (Get-AllTypes $assembly.MainModule.Types)) {
            $target = @($targets | Where-Object { $_.type -ceq $type.FullName })
            if (!$target.Count) { continue }
            $methods = @($type.Methods | Where-Object { $_.Name -cin $target[0].methods } | Sort-Object FullName)
            foreach ($name in $target[0].methods) {
                if (!@($methods | Where-Object { $_.Name -ceq $name }).Count) {
                    $missing += $type.FullName + '::' + $name
                }
            }
            $types += [ordered]@{ assembly=$relative; name=$type.FullName; isEnum=$type.IsEnum;
                methods=@($methods | ForEach-Object {
                    [ordered]@{ signature=$_.FullName; name=$_.Name; isPublic=$_.IsPublic;
                        isStatic=$_.IsStatic; parameters=@($_.Parameters | ForEach-Object {
                            [ordered]@{name=$_.Name;type=$_.ParameterType.FullName;isOut=$_.IsOut;
                                isOptional=$_.IsOptional; hasConstant=$_.HasConstant;
                                constant=$(if ($_.HasConstant) {$_.Constant} else {$null})}
                        }) }
                });
                properties=@($type.Properties | Sort-Object Name | ForEach-Object {
                    [ordered]@{name=$_.Name; type=$_.PropertyType.FullName;
                        canRead=($null -ne $_.GetMethod); canWrite=($null -ne $_.SetMethod)}
                });
                enumValues=@($type.Fields | Where-Object IsLiteral | Sort-Object Name | ForEach-Object {
                    [ordered]@{name=$_.Name;value=$_.Constant}
                }) }
        }
    } finally { $assembly.Dispose() }
}
foreach ($target in $targets) {
    if (!@($types | Where-Object { $_.name -ceq $target.type }).Count) { $missing += $target.type }
}
$manifestPath = Join-Path (Split-Path (Split-Path $GameRoot -Parent) -Parent) 'appmanifest_493520.acf'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8
$build = [regex]::Match($manifest, '"buildid"\s+"(\d+)"')
$appId = [regex]::Match($manifest, '"appid"\s+"(\d+)"')
if (!$build.Success -or !$appId.Success -or $appId.Groups[1].Value -ne '493520') {
    throw 'GTFO Steam app/build identity could not be read.'
}
$gameAssembly = Join-Path $GameRoot 'GameAssembly.dll'
$receipt = [ordered]@{
    schemaVersion='1.0.0'; purpose='forge-map-native-metadata-audit'; verification='metadata-only';
    capturedUtc=[DateTime]::UtcNow.ToString('o');
    game=[ordered]@{appId='493520';build=$build.Groups[1].Value;gameAssemblySha256=(Get-Sha $gameAssembly)};
    targetsSha256=(Get-Sha $TargetsFile);
    assemblies=@($assemblies | Sort-Object file);
    types=@($types | Sort-Object name);
    missing=@($missing | Sort-Object);
    limits=@('No native invocation','No hook order or authority proof','No navigation or multiplayer verification')
}
$destination = [IO.Path]::GetFullPath($OutFile)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
[IO.File]::WriteAllText($destination, ($receipt | ConvertTo-Json -Depth 18) + "`n", [Text.UTF8Encoding]::new($false))
[ordered]@{output=$destination;types=$types.Count;missing=$missing.Count;
    methods=($types | ForEach-Object {$_.methods.Count} | Measure-Object -Sum).Sum;
    build=$receipt.game.build;verification=$receipt.verification} | ConvertTo-Json -Compress
if ($missing.Count) { throw 'Required native metadata missing; inspect the capture.' }
