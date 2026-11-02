"""The sidebar tab 战甲开合 and its small buttons: the piece's way of opening (开法, with its few settings; the viewport shows the
path), layer, batch, row and timing, fragments, still parts, playback, checks (允许 / 要修), the pilot, cutting a piece into strips (切成几条; the two-line strip cut is the 四肢外甲条 click preset), and the import wizard."""
import json
import math
import os

import bpy
from bpy_extras.io_utils import ImportHelper
from mathutils import Matrix, Vector

from . import api, body, checks, click, draw, model, motion, moves

ROW_LABEL = {"": "—", "top": "上", "mid": "中", "low": "下"}
# The ways of opening the panel offers, and which of the scene's fh_way_* settings each one uses (preset argument: setting).
# Group ways act on every selected piece, the active one first.
WAYS = {"hinge": {"deg": "deg"}, "slide": {"direction": "dir", "distance": "dist", "under": "under", "depth": "lift"},
        "offset": {"direction": "dir", "distance": "dist", "lift": "lift"},
        "stepped": {"direction": "dir", "distance": "dist", "rise": "lift", "steps": "steps"},
        "plug": {"push": "lift", "edge": "dir", "deg": "deg"}, "edge": {"edge": "dir", "deg": "deg"},
        "twist": {"deg": "deg"}, "flare": {"lift": "lift"}, "nest": {"toward": "dir", "distance": "dist"},
        "zlink": {"direction": "dir", "deg": "deg"}, "scissor": {"direction": "dir", "distance": "dist"},
        "scissor_door": {"pivot": "dir", "deg": "deg"},
        "louver": {"direction": "dir", "distance": "dist"}, "petal": {"deg": "deg"}, "iris": {"deg": "deg"},
        "fold": {"deg": "deg"}, "spine": {"distance": "dist"}}
# Picking a way puts its own starting settings in the shared fields (a twist is 15 degrees, a Z swing 135).
WAY_DEFAULTS = {"slide": {"dir": "up", "lift": 0.025}, "offset": {"dir": "up", "lift": 0.025},
                "stepped": {"dir": "up", "lift": 0.015}, "plug": {"dir": "neck", "deg": 50.0, "lift": 0.03},
                "edge": {"dir": "hinge", "deg": 60.0}, "twist": {"deg": 15.0}, "flare": {"lift": 0.0015},
                "nest": {"dir": "limb"}, "zlink": {"dir": "outside", "deg": 135.0}, "scissor": {"dir": "out"},
                "scissor_door": {"dir": "hinge", "deg": 45.0}, "louver": {"dir": "up"}, "petal": {"deg": 90.0},
                "iris": {"deg": 40.0}, "fold": {"deg": 60.0}}
GROUP_WAYS = ("louver", "petal", "iris", "fold", "spine")
THEN_HINGE = ("twist", "flare")      # these unlatch first, then the piece turns on its hinge
WAY_LABEL = dict(moves.LABEL, louver="百叶", petal="花瓣", iris="光圈", fold="折叠门", spine="主梁先退")
SETTING_LABEL = {"dir": "方向", "dist": "距离", "lift": "高度", "steps": "段数", "deg": "角度", "under": "上下"}
DIRECTIONS = ("up", "down", "back", "front", "outside", "neck", "out", "in", "limb", "limb-", "hinge")
DIRECTION_LABEL = {"up": "上", "down": "下", "back": "后", "front": "前", "outside": "外侧", "neck": "颈侧", "out": "法线外",
                   "in": "法线内", "limb": "顺肢体", "limb-": "逆肢体", "hinge": "铰链"}
FAIL_TINT = (1.0, 0.1, 0.1, 0.55)


def active_hinge(context):
    return model.hinge_of(context.object)


def refresh_overlays(context=None):
    click.overlay_hinges()
    scene = (context or bpy.context).scene
    items = []
    try:
        rows = json.loads(scene.get("fh_check", "{}")).get("hinges", {})
    except ValueError:
        rows = {}
    for name, row in rows.items():
        h = bpy.data.objects.get(name)
        if h is None or not row.get("failed"):
            continue
        for o in model.pieces_of(h):
            items.append(("tris", draw.mesh_tris(o, 0.002), FAIL_TINT))
    draw.set_layer("fh_check", items)
    guide = []
    j = body.joints(scene)
    if j is not None and scene.fh_show_pilot:
        for a, b, r in body.pilot(j):
            guide.append(("lines", draw.capsule(Vector(a), Vector(b), r), (0.3, 1.0, 1.0, 0.9), True, 1.5))
    draw.set_layer("fh_guides", guide)


# -------------------------------------------------------------------------------------------------------------- operators
class FH_OT_set(bpy.types.Operator):
    """Set one property of the selected piece's hinge"""
    bl_idname = "fh.set"
    bl_label = "设置"
    bl_options = {"REGISTER", "UNDO"}
    key: bpy.props.StringProperty()
    value: bpy.props.StringProperty()

    def execute(self, context):
        h = active_hinge(context)
        if h is None:
            return {"CANCELLED"}
        if self.key == "flip":
            h["fh_angle"] = -h["fh_angle"]
        elif self.key == "fh_layer":
            h["fh_layer"] = self.value
            h["fh_angle"] = math.copysign(motion.ANGLE[self.value], h["fh_angle"] or 1)
        else:
            h[self.key] = self.value
        refresh_overlays(context)
        return {"FINISHED"}


class FH_OT_way(bpy.types.Operator):
    """Open the selected piece (or, for a group way, the selected pieces) this way with the settings shown"""
    bl_idname = "fh.way"
    bl_label = "应用开法"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        scene = context.scene
        kind = scene.fh_way
        h = active_hinge(context)
        if h is None:
            return {"CANCELLED"}
        settings = {}
        for arg, key in WAYS[kind].items():
            v = getattr(scene, "fh_way_" + key)
            if key == "dist" and v <= 0:
                continue          # 0: the preset's own length
            settings[arg] = {"under": {"over": 1, "flat": 0, "under": -1}}.get(key, {}).get(v, v)
        try:
            if kind in GROUP_WAYS:
                hs = [h] + sorted({model.hinge_of(o) for o in context.selected_objects} - {h, None},
                                  key=lambda x: int(x.get("fh_no", 0)))
                api.move_group([int(x["fh_no"]) for x in hs], kind, **settings)
            else:
                api.move(int(h["fh_no"]), kind, then=("hinge", {}) if kind in THEN_HINGE else None, **settings)
        except ValueError as e:
            self.report({"WARNING"}, str(e))
            return {"CANCELLED"}
        refresh_overlays(context)
        return {"FINISHED"}


class FH_OT_timing(bpy.types.Operator):
    """Start the selected piece earlier or later (frames)"""
    bl_idname = "fh.timing"
    bl_label = "时间"
    bl_options = {"REGISTER", "UNDO"}
    step: bpy.props.IntProperty()

    def execute(self, context):
        h = active_hinge(context)
        if h is None:
            return {"CANCELLED"}
        h["fh_delay"] = int(h.get("fh_delay", 0)) + self.step
        return {"FINISHED"}


class FH_OT_split(bpy.types.Operator):
    """Cut the selected piece lengthwise into that many strips, each hinged on the cut it hangs from"""
    bl_idname = "fh.split"
    bl_label = "切成几条"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        h = active_hinge(context)
        if h is None:
            return {"CANCELLED"}
        try:
            api.split(h["fh_no"], count=context.scene.fh_split_count)
        except ValueError as e:
            self.report({"WARNING"}, str(e))
            return {"CANCELLED"}
        return {"FINISHED"}


def _run(op, context, call):
    """Call an api function for an operator: its ValueError becomes a warning."""
    try:
        return call()
    except (ValueError, KeyError) as e:
        op.report({"WARNING"}, str(e))
        return None


class FH_OT_auto_strip(bpy.types.Operator):
    """Cut the selected plate along the lines drawn on the chosen limb (in the annotation layer you draw in) and hinge the strips"""
    bl_idname = "fh.auto_strip"
    bl_label = "自动切条"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        o = context.object
        layer = next((a.layers[a.layers.active_index].info for a in bpy.data.annotations if len(a.layers)), None)
        if o is None or o.type != "MESH" or layer is None:
            self.report({"WARNING"}, "选一块甲，并在标注层上画好线")
            return {"CANCELLED"}
        nos = _run(self, context, lambda: api.auto_strip(o, layer, context.scene.fh_limb))
        if nos is None:
            return {"CANCELLED"}
        self.report({"INFO"}, "新件 %s" % "、".join(str(n) for n in nos))
        return {"FINISHED"}


class FH_OT_auto_back6(bpy.types.Operator):
    """Cut the selected back plate into six blocks by the drawn middle line and the two heights"""
    bl_idname = "fh.auto_back6"
    bl_label = "背部六块"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        o = context.object
        if o is None or o.type != "MESH":
            return {"CANCELLED"}
        nos = _run(self, context, lambda: api.auto_back6(o, (context.scene.fh_row_z1, context.scene.fh_row_z2)))
        if nos is None:
            return {"CANCELLED"}
        self.report({"INFO"}, "新件 %s" % "、".join(str(n) for n in nos))
        return {"FINISHED"}


class FH_OT_auto_attach(bpy.types.Operator):
    """Fold the small loose parts on the selected piece into it"""
    bl_idname = "fh.auto_attach"
    bl_label = "并入挂件"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        h = active_hinge(context)
        if h is None:
            return {"CANCELLED"}
        names = _run(self, context, lambda: api.auto_attach(h["fh_no"]))
        if names is None:
            return {"CANCELLED"}
        self.report({"INFO"}, "并入 %d 个" % len(names))
        return {"FINISHED"}


class FH_OT_orphans(bpy.types.Operator):
    """List the fragments no piece has taken in, or join them into the nearest part"""
    bl_idname = "fh.orphans"
    bl_label = "找碎片"
    bl_options = {"REGISTER", "UNDO"}
    fix: bpy.props.BoolProperty()

    def execute(self, context):
        found = api.orphans(fix=self.fix)
        for f in found[:8]:
            self.report({"INFO"}, "%s %s（%d 面）%s" % (f["kind"], f["name"], f["faces"], "→ " + f["merged"] if f.get("merged") else ""))
        self.report({"INFO"}, "%d 处碎片" % len(found))
        return {"FINISHED"}


class FH_OT_loose(bpy.types.Operator):
    """List what is in no piece and not marked still, or fold the small parts sitting on a door into that door"""
    bl_idname = "fh.loose"
    bl_label = "漏件"
    bl_options = {"REGISTER", "UNDO"}
    fix: bpy.props.BoolProperty()

    def execute(self, context):
        if self.fix:
            done = api.attach_loose()
            self.report({"INFO"}, "并入 %d 块：%s" % (sum(len(v) for v in done.values()),
                                                  "、".join("%d号 %d" % (k, len(v)) for k, v in sorted(done.items()))))
            return {"FINISHED"}
        found = api.unregistered()
        on_door = [f for f in found if f["door"] is not None]
        for f in on_door[:8]:
            self.report({"INFO"}, "%s（%d 面）贴在 %d 号上" % (f["name"], f["faces"], f["door"]))
        self.report({"INFO"}, "未登记 %d 块（在 %s），贴门 %d 块" % (len(found), "、".join(sorted({f["name"] for f in found})),
                                                         len(on_door)))
        return {"FINISHED"}


class FH_OT_flip_dir(bpy.types.Operator):
    """Turn this piece the other way round its hinge (kept only if it then opens the wanted way better)"""
    bl_idname = "fh.flip_dir"
    bl_label = "翻转"
    bl_options = {"REGISTER", "UNDO"}
    hinge: bpy.props.StringProperty()

    def execute(self, context):
        h = bpy.data.objects.get(self.hinge)
        if h is None:
            return {"CANCELLED"}
        r = _run(self, context, lambda: api.flip_dir(h["fh_no"]))
        if r is None:
            return {"CANCELLED"}
        api.check()
        self.report({"INFO"}, "%d号 方向 %s → %s%s" % (r["no"], r["before"], r["after"], "" if r["kept"] else "（没变好，已还原）"))
        return {"FINISHED"}


class FH_OT_fit_all(bpy.types.Operator):
    """Find each hinged piece's largest angle that passes the body, start, float and clash checks"""
    bl_idname = "fh.fit_all"
    bl_label = "找最大开角"

    def execute(self, context):
        rows = api.fit_all()
        less = [r for r in rows if r["best"] is None or r["best"] < r["now"] - 1]
        self.report({"INFO"}, "%d 件，%d 件的现有角度过大（详情在控制台）" % (len(rows), len(less)))
        return {"FINISHED"}


class FH_OT_pack(bpy.types.Operator):
    """Write the review pack (GIFs, overview, table, shoulder and back sheets, check summary) next to the file"""
    bl_idname = "fh.pack"
    bl_label = "出审阅包"

    def execute(self, context):
        if not bpy.data.filepath:
            self.report({"WARNING"}, "先保存这个文件")
            return {"CANCELLED"}
        out = bpy.path.abspath("//审阅")
        paths = api.summary_sheet(out, os.path.splitext(os.path.basename(bpy.data.filepath))[0])
        self.report({"INFO"}, "已写入 %s（%d 个文件）" % (out, len(paths)))
        return {"FINISHED"}


class FH_OT_undo_piece(bpy.types.Operator):
    """Put the selected piece back where it was cut from"""
    bl_idname = "fh.undo_piece"
    bl_label = "撤回此件"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        h = active_hinge(context)
        if h is None:
            return {"CANCELLED"}
        api.remove(h["fh_no"])
        return {"FINISHED"}

class FH_OT_merge_fragment(bpy.types.Operator):
    """Join the selected fragment into the piece it touches"""
    bl_idname = "fh.merge_fragment"
    bl_label = "并入相邻件"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        frag = context.object
        if frag is None or frag.type != "MESH":
            return {"CANCELLED"}
        try:
            api.merge(frag)
        except ValueError as e:
            self.report({"WARNING"}, str(e))
            return {"CANCELLED"}
        return {"FINISHED"}

class FH_OT_delete_fragment(bpy.types.Operator):
    """Delete the selected fragment"""
    bl_idname = "fh.delete_fragment"
    bl_label = "删除"
    bl_options = {"REGISTER", "UNDO"}

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)

    def execute(self, context):
        for o in [o for o in context.selected_objects if o.type == "MESH"]:
            api.delete(o)
        return {"FINISHED"}

class FH_OT_still(bpy.types.Operator):
    """Mark the selected parts as never opening (or clear the mark)"""
    bl_idname = "fh.still"
    bl_label = "标记不动"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        objs = [o for o in context.selected_objects if o.type == "MESH" and not o.get("fh_hinge")]
        on = not all(o.get("fh_still") for o in objs)
        for o in objs:
            if on:
                o["fh_still"] = 1
            elif "fh_still" in o:
                del o["fh_still"]
        return {"FINISHED"}


class FH_OT_play(bpy.types.Operator):
    """Play the whole opening and closing"""
    bl_idname = "fh.play"
    bl_label = "播放整套"

    def execute(self, context):
        stop_playing(context)
        try:
            r = api.play()
        except ValueError as e:
            self.report({"WARNING"}, str(e))
            return {"CANCELLED"}
        self.report({"INFO"}, "打开 %.2f 秒，共 %.2f 秒" % (r["openSeconds"], r["seconds"]))
        return {"FINISHED"}

def stop_playing(context):
    if context.screen.is_animation_playing:
        bpy.ops.screen.animation_cancel(restore_frame=False)


class FH_OT_reset(bpy.types.Operator):
    """Stop and put every piece back"""
    bl_idname = "fh.reset"
    bl_label = "复位"

    def execute(self, context):
        stop_playing(context)
        model.shut_all(context.scene)
        context.scene.frame_set(1)
        refresh_overlays(context)
        return {"FINISHED"}


class FH_OT_check(bpy.types.Operator):
    """Run the checks against the hinges as set"""
    bl_idname = "fh.check"
    bl_label = "检查"

    def execute(self, context):
        stop_playing(context)
        rows, _ = api.check()
        bad = sum(1 for r in rows.values() if r["failed"])
        self.report({"INFO"}, "%d 件，%d 件要看" % (len(rows), bad))
        return {"FINISHED"}

class FH_OT_answer(bpy.types.Operator):
    """允许: keep this as it is and stop listing it. 要修: select the piece"""
    bl_idname = "fh.answer"
    bl_label = "处理"
    bl_options = {"REGISTER", "UNDO"}
    hinge: bpy.props.StringProperty()
    item: bpy.props.StringProperty()
    allow: bpy.props.BoolProperty()

    def execute(self, context):
        h = bpy.data.objects.get(self.hinge)
        if h is None:
            return {"CANCELLED"}
        if self.allow:
            api.allow(h["fh_no"], self.item)
        else:
            for o in context.selected_objects:
                o.select_set(False)
            ps = model.pieces_of(h)
            for o in ps:
                o.select_set(True)
            if ps:
                context.view_layer.objects.active = ps[0]
        refresh_overlays(context)
        return {"FINISHED"}


class FH_OT_import(bpy.types.Operator, ImportHelper):
    """Import a new suit (FBX or GLB), scale it to its height, check its rig and split it into candidate plates"""
    bl_idname = "fh.import_suit"
    bl_label = "导入新战甲"
    bl_options = {"REGISTER", "UNDO"}
    filter_glob: bpy.props.StringProperty(default="*.fbx;*.glb;*.gltf", options={"HIDDEN"})
    height: bpy.props.FloatProperty(name="身高", default=2.3, min=0.5, max=10.0, unit="LENGTH")

    def execute(self, context):
        before = set(bpy.data.objects)
        ext = os.path.splitext(self.filepath)[1].lower()
        if ext == ".fbx":
            bpy.ops.import_scene.fbx(filepath=self.filepath)
        else:
            bpy.ops.import_scene.gltf(filepath=self.filepath)
        new = [o for o in bpy.data.objects if o not in before]
        meshes = [o for o in new if o.type == "MESH"]
        arms = [o for o in new if o.type == "ARMATURE"]
        if not meshes:
            self.report({"ERROR"}, "文件里没有网格")
            return {"CANCELLED"}
        pts = [o.matrix_world @ v.co for o in meshes for v in o.data.vertices]
        lo = min(p.z for p in pts)
        hi = max(p.z for p in pts)
        s = self.height / max(hi - lo, 1e-6)
        roots = [o for o in new if o.parent is None]
        for o in roots:
            o.matrix_world = Matrix.Translation((0, 0, -lo * s)) @ Matrix.Scale(s, 4) @ o.matrix_world
        bpy.ops.object.select_all(action="DESELECT")
        for o in meshes:
            o.select_set(True)
        context.view_layer.objects.active = meshes[0]
        bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        # Candidate plates: one object per material, then per loose part.
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.separate(type="MATERIAL")
        bpy.ops.object.mode_set(mode="OBJECT")
        parts = [o for o in context.selected_objects if o.type == "MESH"]
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.separate(type="LOOSE")
        bpy.ops.object.mode_set(mode="OBJECT")
        parts = [o for o in context.selected_objects if o.type == "MESH"]
        bones = len(arms[0].data.bones) if arms else 0
        self.report({"INFO"}, "%d 块候选甲片；骨架%s" % (len(parts), "：%d 根骨骼" % bones if bones else "：没有"))
        refresh_overlays(context)
        return {"FINISHED"}


# ------------------------------------------------------------------------------------------------------------------ panel
class FH_PT_main(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "战甲开合"
    bl_label = "战甲开合"

    def draw(self, context):
        scene = context.scene
        lay = self.layout
        col = lay.column(align=True)
        col.prop(scene, "fh_template", expand=True)
        big = lay.row()
        big.scale_y = 2.0
        big.operator("fh.click", icon="RESTRICT_SELECT_OFF")
        h = active_hinge(context)
        if h is not None:
            box = lay.box()
            box.label(text="%s号  %s" % (h.get("fh_no", "?"), context.object.name), icon="MESH_DATA")
            now = model.move_of(h).get("kind", "hinge")
            if model.turns_on_hinge(h):
                r = box.row(align=True)
                r.prop(h, '["fh_angle"]', text="角度")
                r.operator("fh.set", text="", icon="ARROW_LEFTRIGHT").key = "flip"
            else:
                box.label(text="现在：%s" % WAY_LABEL.get(now, now), icon="FORCE_CURVE")
            col = box.column(align=True)
            col.prop(scene, "fh_way", text="")
            for key in dict.fromkeys(WAYS[scene.fh_way].values()):
                col.prop(scene, "fh_way_" + key, text=SETTING_LABEL[key])
            col.operator("fh.way", icon="CHECKMARK")
            r = box.row(align=True)
            for k, label in (("outer", "外层"), ("inner", "内层")):
                op = r.operator("fh.set", text=label, depress=h.get("fh_layer") == k)
                op.key, op.value = "fh_layer", k
            r = box.row(align=True)
            r.label(text="时间 %+d 帧" % int(h.get("fh_delay", 0)))
            r.operator("fh.timing", text="提前").step = -2
            r.operator("fh.timing", text="推后").step = 2
            r = box.row(align=True)
            r.prop(h, '["fh_batch"]', text="批次")
            for k in ("", "top", "mid", "low"):
                op = r.operator("fh.set", text=ROW_LABEL[k], depress=h.get("fh_row", "") == k)
                op.key, op.value = "fh_row", k
            r = box.row(align=True)
            r.operator("fh.undo_piece", icon="LOOP_BACK")
            r = box.row(align=True)
            r.prop(scene, "fh_split_count", text="")
            r.operator("fh.split", icon="MOD_BEVEL")
        r = lay.row(align=True)
        r.operator("fh.merge_fragment")
        r.operator("fh.delete_fragment")
        lay.operator("fh.still", icon="LOCKED")
        r = lay.row(align=True)
        r.operator("fh.play", icon="PLAY")
        r.operator("fh.reset", icon="LOOP_BACK")
        lay.operator("fh.check", icon="CHECKMARK")
        auto = lay.box()
        r = auto.row(align=True)
        r.prop(scene, "fh_limb", text="")
        r.operator("fh.auto_strip", icon="MOD_BEVEL")
        r = auto.row(align=True)
        r.prop(scene, "fh_row_z1", text="")
        r.prop(scene, "fh_row_z2", text="")
        r.operator("fh.auto_back6", icon="MESH_GRID")
        r = auto.row(align=True)
        r.operator("fh.auto_attach", icon="LINKED")
        r.operator("fh.fit_all", icon="DRIVER_ROTATIONAL_DIFFERENCE")
        r = auto.row(align=True)
        r.operator("fh.orphans", text="找碎片").fix = False
        r.operator("fh.orphans", text="并回碎片").fix = True
        r = auto.row(align=True)
        r.operator("fh.loose", text="找漏件").fix = False
        r.operator("fh.loose", text="并入贴门零件").fix = True
        auto.operator("fh.pack", icon="IMAGE_DATA")
        try:
            data = json.loads(scene.get("fh_check", "{}"))
        except ValueError:
            data = {}
        rows = data.get("hinges", {})
        summary = data.get("summary", {})
        if summary.get("pieces"):
            text = "%d 件  打开 %.2f 秒" % (summary["pieces"], summary.get("openSeconds", 0))
            if "wayInCoveredPct" in summary:
                text += "  入口挡 %.0f%%" % summary["wayInCoveredPct"]
            lay.label(text=text)
        for name, row in sorted(rows.items()):
            for item in row.get("failed", []):
                r = lay.row(align=True)
                r.alert = True
                h = bpy.data.objects.get(name)
                r.label(text="%s号 %s" % (h.get("fh_no", "?") if h else "?", checks.describe(row, item)))
                if item == "dir":
                    r.operator("fh.flip_dir").hinge = name
                op = r.operator("fh.answer", text="允许")
                op.hinge, op.item, op.allow = name, item, True
                op = r.operator("fh.answer", text="要修")
                op.hinge, op.item, op.allow = name, item, False
        r = lay.row(align=True)
        r.prop(scene, "fh_show_pilot", toggle=True)
        lay.operator("fh.import_suit", icon="IMPORT")


def _refresh(self, context):
    refresh_overlays(context)


CLASSES = (FH_OT_set, FH_OT_way, FH_OT_timing, FH_OT_split, FH_OT_auto_strip, FH_OT_auto_back6, FH_OT_auto_attach,
           FH_OT_orphans, FH_OT_loose, FH_OT_flip_dir, FH_OT_fit_all, FH_OT_pack, FH_OT_undo_piece, FH_OT_merge_fragment, FH_OT_delete_fragment,
           FH_OT_still, FH_OT_play,
           FH_OT_reset, FH_OT_check, FH_OT_answer, FH_OT_import, FH_PT_main)


def _way_defaults(scene, context):
    for key, v in WAY_DEFAULTS.get(scene.fh_way, {}).items():
        setattr(scene, "fh_way_" + key, v)
    h = active_hinge(context)
    if scene.fh_way == "hinge" and h is not None:
        scene.fh_way_deg = abs(h["fh_angle"])


def register_props():
    bpy.types.Scene.fh_template = bpy.props.EnumProperty(
        name="模板", items=[(k, v["label"], "") for k, v in click.TEMPLATES.items()], default="strip")
    bpy.types.Scene.fh_way = bpy.props.EnumProperty(name="开法", items=[(k, WAY_LABEL[k], "") for k in WAYS], default="hinge",
                                                    update=_way_defaults)
    bpy.types.Scene.fh_way_dir = bpy.props.EnumProperty(name="方向", items=[(k, DIRECTION_LABEL[k], "") for k in DIRECTIONS],
                                                        default="up")
    bpy.types.Scene.fh_way_dist = bpy.props.FloatProperty(name="距离", default=0.0, min=0.0, max=1.0, unit="LENGTH",
                                                          description="0：按件的长度自动")
    bpy.types.Scene.fh_way_lift = bpy.props.FloatProperty(name="高度", default=0.025, min=-0.2, max=0.2, unit="LENGTH")
    bpy.types.Scene.fh_way_steps = bpy.props.IntProperty(name="段数", default=3, min=2, max=8)
    bpy.types.Scene.fh_way_deg = bpy.props.FloatProperty(name="角度", default=60.0, min=-180.0, max=180.0)
    bpy.types.Scene.fh_way_under = bpy.props.EnumProperty(name="上下", items=[("over", "滑到上面", ""), ("flat", "贴面", ""),
                                                                            ("under", "滑到下面", "")], default="under")
    bpy.types.Scene.fh_split_count = bpy.props.IntProperty(name="条数", default=2, min=2, max=12)
    bpy.types.Scene.fh_limb = bpy.props.EnumProperty(
        name="肢体", items=[("%s.%s" % (p, s), "%s%s" % ("右" if s == "R" else "左", api.LIMB_ZH[p]), "")
                          for p in ("thigh", "shin", "upperarm", "forearm") for s in ("R", "L")], default="thigh.R")
    bpy.types.Scene.fh_row_z1 = bpy.props.FloatProperty(name="上横线高", default=1.75, unit="LENGTH")
    bpy.types.Scene.fh_row_z2 = bpy.props.FloatProperty(name="下横线高", default=1.55, unit="LENGTH")
    bpy.types.Scene.fh_show_pilot = bpy.props.BoolProperty(name="飞行员", default=False, update=_refresh)


def unregister_props():
    for p in ("fh_template", "fh_way", "fh_way_dir", "fh_way_dist", "fh_way_lift", "fh_way_steps", "fh_way_deg", "fh_way_under",
              "fh_split_count", "fh_limb", "fh_row_z1", "fh_row_z2", "fh_show_pilot"):
        delattr(bpy.types.Scene, p)
