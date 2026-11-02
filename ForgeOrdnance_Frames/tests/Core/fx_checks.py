"""The exosuit effects table in Native/FrameFx.cs (system, texture, blend, capacity) matches the systems the bundle build authors in
tools/fx/FrameFxBuild.cs, and the package ships no shader of its own: its particles use ForgeVisual's."""
from pathlib import Path
import re
import sys
import unittest

sys.dont_write_bytecode = True
PACKAGE = Path(__file__).parents[2]


def built():
    source = (PACKAGE / "tools/fx/FrameFxBuild.cs").read_text(encoding="utf-8")
    out = []
    for name, texture, rest in re.findall(r'new Spec \{ Name = "(\w+)", Texture = "([\w-]+)"(.*?)\n\s*(?=//|new Spec|\};)', source, re.S):
        maximum = int(re.search(r"\bMax = (\d+)", rest).group(1))
        out.append((name, texture, "Additive" if re.search(r"\bAdd = true", rest) else "Alpha", maximum))
    return out


def table():
    source = (PACKAGE / "Native/FrameFx.cs").read_text(encoding="utf-8")
    return [(n, t, b, int(c)) for n, t, b, c in re.findall(r'\("(\w+)", "([\w-]+)", FxBlend\.(\w+), (\d+)\)', source)]


class FxChecks(unittest.TestCase):
    def test_table_matches_the_bundle_build(self):
        self.assertEqual(len(built()), 20)
        self.assertEqual(sorted(table()), sorted(built()))

    def test_table_follows_the_kind_order(self):
        source = (PACKAGE / "Native/FrameFx.cs").read_text(encoding="utf-8")
        kinds = re.search(r"enum Kind\s*\{(.*?)\}", source, re.S).group(1)
        self.assertEqual([n for n, _, _, _ in table()], re.findall(r"\w+", kinds))

    def test_no_shader_of_its_own(self):
        self.assertEqual([p.name for p in PACKAGE.rglob("*.shader") if p.name != "PreviewUnlit.shader"], [])


if __name__ == "__main__":
    unittest.main()