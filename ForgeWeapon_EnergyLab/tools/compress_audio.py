"""Losslessly compress the source PCM for CLR embedded resources; no audio codec dependency.

Invoked by the csproj. The source PCM and its provenance stay unchanged.
"""
import argparse
import gzip
from pathlib import Path


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--source", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    opt = ap.parse_args()
    files = sorted(opt.source.glob("*.bin"))
    if not files:
        raise ValueError("No PCM cues in " + str(opt.source))
    opt.out.mkdir(parents=True, exist_ok=True)
    raw_bytes, packed_bytes, expected = 0, 0, set()
    for source in files:
        raw = source.read_bytes()
        packed = gzip.compress(raw, compresslevel=9, mtime=0)
        if gzip.decompress(packed) != raw:
            raise ValueError("Audio round trip differs: " + str(source))
        dest = opt.out / (source.name + ".gz")
        expected.add(dest.name)
        if not dest.is_file() or dest.read_bytes() != packed:
            temporary = dest.with_suffix(".tmp")
            temporary.write_bytes(packed)
            temporary.replace(dest)
        raw_bytes += len(raw)
        packed_bytes += len(packed)
    for stale in opt.out.glob("*.bin.gz"):
        if stale.name not in expected:
            stale.unlink()
    print(f"ENERGY_AUDIO_LOSSLESS {len(files)} cues {raw_bytes} -> {packed_bytes} bytes; all PCM bytes identical")


if __name__ == "__main__":
    main()
