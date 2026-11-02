"""战甲开合: cut the plates that open, set their hinges by clicking and pick how each opens (a hinge, slides, linkages:
moves.py, all one path model in motion.py), in Blender 5.2 (sidebar tab 战甲开合).

Works for any model with hinged parts; the presets and the pilot checks are for the exosuits. What the owner sets is stored
as custom properties (model.py); api.py drives the same from scripts; build.ps1 bakes it and builds the suit. Load it with tools/hinge_tool/open.ps1, or
install the folder as an extension (blender_manifest.toml)."""
import bpy

from . import click, draw, panel

bl_info = {
    "name": "战甲开合",
    "author": "NAinfini",
    "version": (1, 0, 0),
    "blender": (5, 2, 0),
    "location": "3D 视图 > 侧栏 > 战甲开合",
    "category": "Object",
}


def register():
    panel.register_props()
    bpy.utils.register_class(click.FH_OT_click)
    for c in panel.CLASSES:
        bpy.utils.register_class(c)
    draw.register()


def unregister():
    draw.unregister()
    for c in reversed(panel.CLASSES):
        bpy.utils.unregister_class(c)
    bpy.utils.unregister_class(click.FH_OT_click)
    panel.unregister_props()
