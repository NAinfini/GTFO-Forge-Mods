"""Build the ForgeFrameGen Thunderstore zip from the Release build output; never publishes.

    pwsh Mods/ForgeFrameGen/tools/build_native.ps1
    dotnet build Mods/ForgeFrameGen/ForgeFrameGen.csproj -c Release --artifacts-path <dotnet artifacts> -p:GTFOBepInExPath=<profile>/BepInEx
    python Mods/ForgeFrameGen/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>

The build puts the native bridge, NVIDIA's two DLSS libraries, the five Streamline libraries, AMD's three FSR libraries and the vendors'
licences beside the plugin DLL; the zip carries them as they are, with the preloader patcher under patchers/. Each vendor file is checked
against its pin in dlss-sdk.json, streamline-sdk.json or fidelityfx-sdk.json, so only the unchanged vendor file is ever packaged.
"""
from pathlib import Path
import json
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Build"))
import package_common


METADATA = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE", "lang/en.forge", "lang/zh.forge")
NVIDIA = {"nvngx_dlss.dll": "lib/Windows_x86_64/rel/nvngx_dlss.dll", "nvngx_dlssg.dll": "lib/Windows_x86_64/rel/nvngx_dlssg.dll", "NVIDIA-RTX-SDKs-LICENSE.txt": "LICENSE.txt"}
STREAMLINE = {name: "bin/x64/" + name for name in ("sl.interposer.dll", "sl.common.dll", "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll")}
STREAMLINE["NVIDIA-Streamline-LICENSE.txt"] = "license.txt"
STREAMLINE["NVIDIA-Reflex-LICENSE.txt"] = "bin/x64/reflex.license.txt"
AMD = {name: "Kits/FidelityFX/signedbin/" + name for name in ("amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll")}
AMD["AMD-FSR-SDK-LICENSE.txt"] = "Kits/FidelityFX/docs/license.md"


def extra(folder: Path, built: Path) -> dict[str, Path]:
    dlss = json.loads((folder / "dlss-sdk.json").read_text(encoding="utf-8"))["files"]
    streamline = json.loads((folder / "streamline-sdk.json").read_text(encoding="utf-8"))["files"]
    fidelityfx = json.loads((folder / "fidelityfx-sdk.json").read_text(encoding="utf-8"))["files"]
    content = {"ForgeFrameGen.Native.dll": built / "ForgeFrameGen.Native.dll", "RiskOfRainUpscaling-LICENSE.txt": folder / "RiskOfRainUpscaling-LICENSE.txt",
               "patchers/ForgeFrameGen.Preloader.dll": built.parent.parent / "ForgeFrameGen.Preloader" / "release" / "ForgeFrameGen.Preloader.dll"}
    for name, source, pins in [(n, s, dlss) for n, s in NVIDIA.items()] + [(n, s, streamline) for n, s in STREAMLINE.items()] + [(n, s, fidelityfx) for n, s in AMD.items()]:
        path = built / name
        if not path.is_file():
            raise ValueError(str(path) + " is missing; run tools/build_native.ps1 and the Release build")
        if package_common.sha256_file(path) != pins[source]:
            raise ValueError(str(path) + " differs from the vendor's pinned " + source + "; only the unchanged vendor file may ship")
        content[name] = path
    if not content["ForgeFrameGen.Native.dll"].is_file():
        raise ValueError(str(content["ForgeFrameGen.Native.dll"]) + " is missing; run tools/build_native.ps1 and the Release build")
    return content


if __name__ == "__main__":
    raise SystemExit(package_common.main("ForgeFrameGen", ("ForgeFrameGen.dll",), extra, METADATA))
