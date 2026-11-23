"""Test-profile installation, byte-preserving r2modman entries and game-session provenance guards (level capture, pack tests)."""
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import zipfile
import sys
# In the repository safe_delete sits one folder up; a copied tooling folder holds it beside this file.
sys.path[:0] = [str(Path(__file__).resolve().parent), str(Path(__file__).resolve().parents[1])]
import safe_delete  # noqa: E402
from game_process import IN_REPOSITORY, profiles_root, require_game_lock  # noqa: E402


# Which profiles a tool may write, by purpose. An automatic drive session (it rewrites configs and drops command and trace files)
# and an install both go to the owner's Forge-Multiplayer profile (he allowed drive sessions there; never while he runs the game).
# An install is run on the owner's explicit request and always names the profile (there is no default install profile);
# Forge-TimeAttack is the competition profile he asked for (time-attack Rundown and its clean dependencies only).
# The list binds this repository's tools; a copied tooling folder drives whichever profile its user names.
ALLOWED_PROFILES = {"drive": frozenset({"Forge-Multiplayer", "Forge-TimeAttack"}), "install": frozenset({"Forge-Multiplayer", "Forge-TimeAttack"})}
_PROFILE_ROOT = profiles_root()
DEFAULT_PROFILE = _PROFILE_ROOT / "Forge-Multiplayer"


def require_plain_path(path):
    for parent in (Path(path), *Path(path).parents):
        if parent.is_symlink() or (parent.exists() and getattr(parent.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            raise RuntimeError("输入或输出路径是重解析点，已停止：" + str(parent))


def require_profile(profile, purpose="drive"):
    profile = Path(profile).absolute()
    if IN_REPOSITORY and profile.name not in ALLOWED_PROFILES[purpose]:
        raise RuntimeError("此用途（" + purpose + "）只允许 profile：" + "、".join(sorted(ALLOWED_PROFILES[purpose])) + "；拒绝修改：" + profile.name)
    require_plain_path(profile)
    if profile.exists():
        list(plain_tree(profile))
    return profile


def package_dir(plugins, name):
    """The one folder of one package under plugins. A name is a single Author-Name identity: a list of names (spaces, commas,
    separators), an empty name, or anything that resolves to the plugins root or outside it is refused, so no install step can ever touch the plugins root itself."""
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+", name):
        raise ValueError("包名必须是单个 作者-名称，不接受包名串：" + repr(name))
    target = Path(plugins) / name
    if target.parent != Path(plugins) or target == Path(plugins):
        raise RuntimeError("包目录不在 plugins 之下，已拒绝：" + str(target))
    return target


def digest(data):
    return hashlib.sha256(data).hexdigest()


DATA_PREFIX = "GameData/"


def data_dir(target):
    """MTFO's folder for one package: BepInEx/GameData/<package>, beside BepInEx/plugins/<package> (target)."""
    return target.parent.parent / "GameData" / target.name


def package_contents(target, record=False):
    """{path: bytes} of one installed package in the zip's layout: its plugin folder as is, its MTFO folder as GameData/<file>.
    The install record is left out unless asked for."""
    files = {}
    for folder, prefix in ((target, ""), (data_dir(target), DATA_PREFIX)):
        if folder.exists():
            files.update({prefix + p.relative_to(folder).as_posix(): p.read_bytes() for p in plain_tree(folder) if record or p.name != INSTALLED_RECORD})
    return files


def package_path(target, relative):
    return data_dir(target) / relative[len(DATA_PREFIX):] if relative.startswith(DATA_PREFIX) else target / relative


def atomic_bytes(path, data):
    require_game_lock(path)
    require_plain_path(path)
    temporary = path.with_name(path.name + ".guidecapture.tmp")
    require_plain_path(temporary)
    with temporary.open("wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def shipped_packages(identity):
    """Every package the release set ships: the base packages and the public libraries ForgeEngine pins."""
    return identity["packages"] + identity.get("libraries", [])


def plain_tree(root):
    """Never traverse profile junctions into another profile or installation."""
    if not root.exists():
        return
    def fail(error): raise error
    for directory, folders, files in os.walk(root, followlinks=False, onerror=fail):
        for name in [*folders, *files]:
            path = Path(directory) / name
            attributes = getattr(path.lstat(), "st_file_attributes", 0)
            if path.is_symlink() or attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise RuntimeError("profile 内存在重解析点，无法证明来源，已停止：" + str(path))
        for name in files:
            yield Path(directory) / name


INSTALLED_RECORD = ".forge-installed.json"


def installed_package_files(root):
    """{path: sha256} of the files we installed for a package (plugin folder files, and its data tables as GameData/<file>): the
    record install_export_pack leaves beside them. A folder installed some other way has no record, and every file in it is taken as ours."""
    record = Path(root) / INSTALLED_RECORD
    if record.is_file():
        return json.loads(record.read_text(encoding="utf-8"))
    return {name: digest(data) for name, data in package_contents(Path(root)).items()}


def installed_drift(root, installed):
    """Compares a package (plugin folder plus its GameData folder) with the file list we installed ({path: sha256}). Only those files are ours:
    a changed or missing one is drift. A file another mod wrote into the folder (its own config or saves) is
    listed apart and is not drift; no per-mod exception exists."""
    actual = {name: digest(data) for name, data in package_contents(Path(root)).items()}
    return {"changed": sorted(name for name, sha in installed.items() if actual.get(name) != sha),
            "unmanaged": sorted(name for name in actual if name not in installed)}


def package_state(plugins, name):
    """What one installed package is now, for the install log: its manifest.json version and the sha256 of its main dll (the dll
    named like the package, else the only dll in the folder); None where the package or the file is absent or ambiguous."""
    folder = package_dir(plugins, name)
    if not folder.exists():
        return {"packageName": name, "version": None, "mainDllSha256": None}
    list(plain_tree(folder))
    manifest = next(folder.rglob("manifest.json"), None)
    dlls = sorted(folder.rglob("*.dll"))
    main = next((p for p in dlls if p.stem == name.split("-", 1)[1]), dlls[0] if len(dlls) == 1 else None)
    return {"packageName": name,
            "version": json.loads(manifest.read_text(encoding="utf-8-sig")).get("version_number") if manifest else None,
            "mainDllSha256": digest(main.read_bytes()) if main else None}


# r2modman's install rules for GTFO (the installRules of its GTFO ecosystem entry), in its order: the folder under BepInEx/,
# the file extensions sent there when a file sits in no such folder, and whether the package gets its own <package>/ folder
# there (subdir tracking) or its files land in the folder itself (none and state tracking). plugins is the default location.
MANAGER_ROUTES = (("plugins", (".dll",), True), ("core", (), True), ("patchers", (), True), ("monomod", (".mm.dll",), True),
                  ("config", (), False), ("GameData", (), True), ("Assets", (), False))


def manager_layout_files(archive):
    """{(route, path): bytes} of one package zip placed by r2modman's rules (MANAGER_ROUTES): an entry inside a folder named like
    a route, at any depth and in any case, keeps its path below that folder; any other file goes by its name alone to the route
    its extension names, else to plugins (so the root manifest.json, icon.png, README.md and CHANGELOG.md sit in the plugin
    folder). path is below BepInEx/<route>/<package>/ or BepInEx/<route>/ (installed_path). A traversal, or two entries
    landing on one path, is refused."""
    routes = {route.lower(): route for route, _, _ in MANAGER_ROUTES}
    with zipfile.ZipFile(archive) as source:
        files = {}
        for name in source.namelist():
            if name.endswith("/"):
                continue
            parts = name.split("/")
            if "\\" in name or any(part in ("", ".", "..") for part in parts):
                raise ValueError("zip 含不安全的条目，未安装：" + name)
            folder = next((i for i, part in enumerate(parts[:-1]) if part.lower() in routes), None)
            if folder is not None:
                key = (routes[parts[folder].lower()], "/".join(parts[folder + 1:]))
            else:
                by_extension = [(len(extension), route) for route, extensions, _ in MANAGER_ROUTES for extension in extensions if name.lower().endswith(extension)]
                key = (max(by_extension)[1] if by_extension else "plugins", parts[-1])
            if key in files:
                raise ValueError("zip 里两个条目落到同一位置：" + "/".join(key))
            files[key] = source.read(name)
    return files


def installed_path(route, path, package):
    """The BepInEx-relative path of one manager_layout_files entry of package."""
    owned = next(owned for name, _, owned in MANAGER_ROUTES if name == route)
    return (package_dir(Path(route), package) / path if owned else Path(route) / path).as_posix()


def export_pack_files(archive):
    """(package, {path: bytes}) of one exported Rundown or mod zip (manager_layout_files) in the installed package's layout
    (package_contents: its plugin folder as is, its MTFO folder as GameData/<file>); an entry placed anywhere else, which the
    drift record cannot follow, is refused. The Thunderstore manifest names no team, so the package is the
    `package` of its identity document (forge/rundown.forge, else forge/content/pack.forge)."""
    files = {}
    for (route, path), data in manager_layout_files(archive).items():
        relative = path if route == "plugins" else DATA_PREFIX + path
        if route not in ("plugins", "GameData") or (route == "plugins" and path.startswith(DATA_PREFIX)):
            raise ValueError("导出包含插件目录与 GameData 目录以外的条目，未安装：" + route + "/" + path)
        files[relative] = data
    identity = files.get("forge/rundown.forge") or files.get("forge/content/pack.forge")
    if identity is None:
        raise ValueError("导出包缺少身份文件 forge/rundown.forge 或 forge/content/pack.forge，未安装")
    return json.loads(identity.decode("utf-8-sig"))["package"], files


def install_export_pack(bep, archive):
    """Install one exported pack (export_pack_files) as its own package folder of an allowed profile, the tables in the folder
    MTFO reads (BepInEx/GameData/<package>). Nothing is copied: the package being replaced is returned as `replaced` (package_state);
    to go back, install the earlier pack zip again. A refused entry stops it before anything changes."""
    require_profile(Path(bep).parent, "install")
    require_game_lock(bep)
    name, files = export_pack_files(archive)
    target = package_dir(Path(bep) / "plugins", name)
    replaced = package_state(Path(bep) / "plugins", name)
    for folder in (target, data_dir(target)):
        if folder.exists():
            list(plain_tree(folder))
            safe_delete.remove(folder, folder.parent)
    for relative, data in files.items():
        destination = package_path(target, relative)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    actual = {relative: digest(data) for relative, data in package_contents(target).items()}
    if actual != {relative: digest(data) for relative, data in files.items()}:
        raise RuntimeError("安装文件与导出包不同：" + name)
    # What we installed, so later checks compare only these files and not what another mod writes into the folder.
    atomic_bytes(target / INSTALLED_RECORD, json.dumps(actual, ensure_ascii=False, sort_keys=True).encode("utf-8"))
    return {"packageName": name, "zipSha256": digest(Path(archive).read_bytes()), "files": actual, "replaced": replaced,
            "installed": package_state(Path(bep) / "plugins", name)}


def package_zip_files(archive):
    """(package, {BepInEx-relative path: bytes}) of one mod package zip (<Author>-<Name>-<version>.zip, a Forge release or a
    Thunderstore package) as r2modman installs it (manager_layout_files)."""
    name = Path(archive).stem.rsplit("-", 1)[0]
    return name, {installed_path(route, path, name): data for (route, path), data in manager_layout_files(archive).items()}


def install_package_zip(bep, archive):
    """Replace one mod package with the zip's whole content, placed as r2modman places it (package_zip_files): the package's own
    folder in every route that has one is removed first, then every file is written, shared-folder files (Assets/, config/)
    over what is there. A mod's lang/ and media travel with its DLL, so a lane that swaps only the DLL leaves lang keys missing:
    always install the whole package."""
    require_profile(Path(bep).parent, "install")
    require_game_lock(bep)
    bep = Path(bep)
    name, files = package_zip_files(archive)
    replaced = package_state(bep / "plugins", name)
    for route, _, owned in MANAGER_ROUTES:
        target = package_dir(bep / route, name)
        if owned and target.exists():
            list(plain_tree(target))
            safe_delete.remove(target, target.parent)
    for relative, data in files.items():
        destination = bep / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    actual = {relative: digest((bep / relative).read_bytes()) for relative in files}
    if actual != {relative: digest(data) for relative, data in files.items()}:
        raise RuntimeError("安装文件与 zip 不同：" + name)
    return {"packageName": name, "files": actual, "replaced": replaced, "installed": package_state(bep / "plugins", name)}

TEAM_GUIDE = """怎么装

所有人（主机也一样）必须装同一个包，版本不同的人进不了同一局或会不同步。

1. 在 r2modman 里新建一个档，装好这 {count} 个前置：
{prerequisites}
   用这个档启动一次游戏，进到主菜单后关掉。
2. 在 r2modman 的「设置」里点「Browse profile folder」打开档目录，把本 zip 里的 BepInEx 文件夹拖进去，合并并覆盖同名文件。
   以前装过本包的旧版：先删掉档里 BepInEx/plugins 和 BepInEx/GameData 下所有 NAinfini- 开头的文件夹，再拖。
3. 用这个档启动游戏。
"""


def team_prerequisites(manifests):
    """The Thunderstore packages the zip's own packages ({`Author-Name`: manifest}) depend on (each manifest's `dependencies`,
    `Author-Name-Version`) that the zip does not ship itself, one per package at the highest version any manifest asks for."""
    shipped = set(manifests)
    wanted = {}
    for manifest in manifests.values():
        for dependency in manifest.get("dependencies", []):
            package, _, version = dependency.rpartition("-")
            if not package or package in shipped:
                continue
            key = tuple(int(part) for part in version.split(".") if part.isdigit())
            if package not in wanted or key > wanted[package][0]:
                wanted[package] = (key, version)
    return sorted((package, version) for package, (_, version) in wanted.items())


def team_guide(manifests):
    rows = team_prerequisites(manifests)
    lines = ["   - " + package.split("-", 1)[1] + "（作者 " + package.split("-", 1)[0] + "，" + version + " 或更新）" for package, version in rows]
    return TEAM_GUIDE.format(count=len(rows), prerequisites="\n".join(lines))


def build_team_zip(packages, packs, output):
    """The drag-in zip for teammates: one BepInEx/ folder laid out file for file as install-zip (packages) and install-pack (packs)
    put each zip into a profile, plus 怎么装.txt. Built from the release zips, never from a profile, so nothing personal (skins,
    picker choices, config, recordings, logs, caches) can get in."""
    files, manifests = {}, {}
    def add(path, data):
        if path in files:
            raise ValueError("拖入包条目重复：" + path)
        files[path] = data
    placed = [package_zip_files(archive) for archive in packages]
    for name, contents in map(export_pack_files, packs):
        placed.append((name, {package_path(package_dir(Path("plugins"), name), relative).as_posix(): data for relative, data in contents.items()}))
    for (name, contents), archive in zip(placed, [*packages, *packs]):
        manifest = contents.get(installed_path("plugins", "manifest.json", name))
        if manifest is None:
            raise ValueError("包里没有 manifest.json，前置无从得知：" + str(archive))
        manifests[name] = json.loads(manifest.decode("utf-8-sig"))
        for path, data in contents.items():
            add("BepInEx/" + path, data)
    add("怎么装.txt", team_guide(manifests).encode("utf-8-sig"))
    output = Path(output).absolute()
    require_plain_path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as stream:
        for path, data in sorted(files.items()):
            stream.writestr(path, data)
    return {"zip": str(output), "bytes": output.stat().st_size, "sha256": digest(output.read_bytes()), "entries": len(files),
            "packages": sorted({path.split("/")[2] for path in files if path.startswith("BepInEx/plugins/")})}


def compare_team_zip(archive, bep):
    """Read-only: every BepInEx/ entry of a team zip against the same path in a profile (sha256), and the files of the same
    package folders that only the profile has (the install record excepted)."""
    bep = Path(bep)
    with zipfile.ZipFile(archive) as source:
        shipped = {name[len("BepInEx/"):]: digest(source.read(name)) for name in source.namelist() if name.startswith("BepInEx/") and not name.endswith("/")}
    folders = {"/".join(path.split("/")[:2]) for path in shipped}
    profile = {path.relative_to(bep).as_posix(): path for folder in folders for path in plain_tree(bep / folder) if path.name != INSTALLED_RECORD}
    return {"compared": len(shipped),
            "same": sum(1 for path, sha in shipped.items() if path in profile and digest(profile[path].read_bytes()) == sha),
            "different": sorted(path for path, sha in shipped.items() if path in profile and digest(profile[path].read_bytes()) != sha),
            "missingInProfile": sorted(path for path in shipped if path not in profile),
            "onlyInProfile": sorted(path for path in profile if path not in shipped)}


def main(argv=None):
    """install-pack / install-zip on the profile named by --bep (always explicit; Forge-Multiplayer) (its BepInEx folder; the game lock is taken,
    waiting for it, when the profile is under the r2modman profiles root); team-zip writes the drag-in zip from --zip package zips
    and --pack exported packs, and with --bep compares it with that profile, only reading it."""
    import argparse
    import contextlib
    from game_process import game_lock
    parser = argparse.ArgumentParser(description=main.__doc__)
    parser.add_argument("command", choices=("install-pack", "install-zip", "team-zip"))
    parser.add_argument("--bep", type=Path, help="档的 BepInEx 目录，如 …/profiles/Forge-Multiplayer/BepInEx；安装必须显式给出，只接受 Forge-Multiplayer 与比赛档 Forge-TimeAttack（仅在所有者明确要求时）；team-zip 只读它来比对")
    parser.add_argument("--zip", type=Path, action="append", default=[], help="install-pack：导出包（V1、回炉）；install-zip、team-zip：模组发行 zip 或第三方 Thunderstore 包（<作者>-<名称>-<版本>.zip），按 r2modman 的规则落位；可重复")
    parser.add_argument("--pack", type=Path, action="append", default=[], help="team-zip：导出包（V1、回炉）；可重复")
    parser.add_argument("--output", type=Path, help="team-zip 的输出")
    args = parser.parse_args(argv)
    if not args.zip:
        parser.error(args.command + " 需要至少一个 --zip")
    if args.command == "team-zip":
        if not args.output:
            parser.error("team-zip 需要 --output")
        result = build_team_zip(args.zip, args.pack, args.output)
        if args.bep:
            result["profile"] = compare_team_zip(args.output, args.bep.absolute())
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return
    if not args.bep:
        parser.error(args.command + " 需要 --bep")
    bep = args.bep.absolute()
    inside = profiles_root().absolute() in bep.parents
    with game_lock("pack-cli") if inside else contextlib.nullcontext():
        install = install_export_pack if args.command == "install-pack" else install_package_zip
        results = [install(bep, archive) for archive in args.zip]
    print(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()