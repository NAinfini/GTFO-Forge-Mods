"""Review renders of a suit's rocket launcher, as frames.json places it, on the suit's rigged model (build_frame.py's
<model>_rigged.blend, read only), shut and fully open, and the checks that no other plate passes through the launcher while the
suit opens and that its tubes see out.

blender --background --factory-startup <model>_rigged.blend --python preview_rockets.py -- <frames.json> <prefab> <props stage> <out folder> [<shoulder plate bone>]

<props stage> is the folder build_prop.py staged the parts into (<stage>/props). Writes <out>/<prefab>_rockets_shut_{front,back,rear}.png,
<out>/<prefab>_rockets_open_{back,rear}.png and <out>/<prefab>_rockets_size.png (head and shoulders from the front, the launcher,
helmet and a shoulder plate outlined and measured), and prints ROCKETS_FRAME / ROCKETS_CLEAR or ROCKETS_HIT (launcher vertices inside
plates other than its seat over the open clip), ROCKETS_SEAT (how it sits in the armour, shut) and ROCKETS_MUZZLE lines. The seat is
the plate the launcher rides and that plate's parent bone: the launcher may sink into it."""
import bpy, json, math, os, sys
from mathutils import Euler, Matrix, Vector
from mathutils.bvhtree import BVHTree

frames_json, prefab, props_stage, out, *shoulder = sys.argv[sys.argv.index("--") + 1:]
os.makedirs(out, exist_ok=True)
with open(frames_json, encoding="utf-8") as f:
    definitions = json.load(f)
definition = next(d for d in definitions if d["Payload"]["Model"] == prefab)
launcher = definition["Payload"]["Rockets"]["Launcher"]
scene = bpy.context.scene
rig = next(o for o in bpy.data.objects if o.type == "ARMATURE")
body = next(o for o in bpy.data.objects if o.type == "MESH" and o.parent == rig)

# Unity (x right, y up, z forward) to Blender (-x, -z, y): the build's export convention.
M = Matrix(((-1, 0, 0), (0, 0, -1), (0, 1, 0)))


def unity_rotation(pitch, yaw, roll):
    # Unity applies Z, then X, then Y.
    return Matrix.Rotation(math.radians(yaw), 3, "Y") @ Matrix.Rotation(math.radians(pitch), 3, "X") @ Matrix.Rotation(math.radians(roll), 3, "Z")


def material(name, folder):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(os.path.join(folder, "albedo.png"))
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


def import_part(part):
    folder = os.path.join(props_stage, part)
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.join(folder, "model.fbx"), axis_forward="-Z", axis_up="Y")
    new = [o for o in bpy.data.objects if o not in before]
    mesh = next(o for o in new if o.type == "MESH")
    mat = material(part, folder)
    mesh.data.materials.clear()
    mesh.data.materials.append(mat)
    # One holder per part: the imported objects keep their place under it.
    holder = bpy.data.objects.new(part + ".holder", None)
    scene.collection.objects.link(holder)
    for o in new:
        if o.parent is None:
            o.parent = holder
    return holder, mesh, {o.name.split(".")[0]: o for o in new if o.type == "EMPTY"}


def nose(mesh):
    # Furthest point along the part's front (Blender -Y) from its holder.
    return max(-(mesh.matrix_world @ Vector(c)).y for c in mesh.bound_box)


# The launcher's place is in the model's space at its rest (bind) pose, as the game places them.
rig.data.pose_position = "REST"
bpy.context.view_layer.update()
pods = []
rounds = []
carriers = []
muzzles = []
seat_inset = 0.015
for i, mount in enumerate([launcher]):
    holder, mesh, sockets = import_part(launcher["Pod"])
    ux, uy, uz = mount["Position"]
    rot = M @ unity_rotation(*mount["Rotation"]) @ M.transposed()
    holder.matrix_world = Matrix.Translation(Vector((-ux, -uz, uy))) @ rot.to_4x4()
    bpy.context.view_layer.update()
    carriers.append(mount["Bone"])
    world = holder.matrix_world.copy()
    holder.parent = rig
    holder.parent_type = "BONE"
    holder.parent_bone = mount["Bone"]
    bpy.context.view_layer.update()
    holder.matrix_world = world
    pods.append(mesh)
    for t in range(1, 99):
        socket = sockets.get("muzzle_%d" % t)
        if socket is None:
            break
        muzzles.append((socket, holder))
        r_holder, r_mesh, _ = import_part(launcher["Round"])
        bpy.context.view_layer.update()
        reach = nose(r_mesh)
        place = socket.matrix_world.to_translation()
        forward = (holder.matrix_world.to_3x3() @ Vector((0, -1, 0))).normalized()
        r_holder.matrix_world = Matrix.Translation(place - forward * (reach + seat_inset)) @ holder.matrix_world.to_3x3().to_4x4()
        bpy.context.view_layer.update()
        w = r_holder.matrix_world.copy()
        r_holder.parent = holder
        r_holder.matrix_world = w
        rounds.append(r_mesh)

rig.data.pose_position = "POSE"
opening = bpy.data.actions["open"]
rig.animation_data_create()
rig.animation_data.action = opening
if hasattr(rig.animation_data, "action_slot") and len(getattr(opening, "slots", [])) > 0:
    rig.animation_data.action_slot = opening.slots[0]
first, last = int(opening.frame_range[0]), int(opening.frame_range[1])


def world_points(obj):
    deps = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(deps)
    m = ev.to_mesh()
    pts = [ev.matrix_world @ v.co for v in m.vertices]
    polys = [tuple(p.vertices) for p in m.polygons]
    ev.to_mesh_clear()
    return pts, polys


def inside_count(points, tree):
    # A point is inside the shell when a ray from it upward crosses the shell an odd number of times.
    count = 0
    for p in points:
        hits, origin = 0, p.copy()
        for _ in range(32):
            hit = tree.ray_cast(origin, Vector((0.0, 0.0, 1.0)))
            if hit[0] is None:
                break
            hits += 1
            origin = hit[0] + Vector((0, 0, 1e-4))
        count += hits & 1
    return count


# A launcher's seat is the plate it rides and the limb that plate is fixed to while shut (its parent bone): a launcher may sit
# into its seat, so the seat is checked apart from the rest of the shell.
group_names = {g.index: g.name for g in body.vertex_groups}
owner = [group_names[max(v.groups, key=lambda g: g.weight).group] if v.groups else "" for v in body.data.vertices]
seat_bones = set(carriers) | {rig.data.bones[c].parent.name for c in carriers if rig.data.bones[c].parent}


def split_trees(pts, polys):
    seat = [p for p in polys if any(owner[v] in seat_bones for v in p)]
    rest = [p for p in polys if not any(owner[v] in seat_bones for v in p)]
    return BVHTree.FromPolygons(pts, rest), BVHTree.FromPolygons(pts, seat), rest


worst = 0
for frame in list(range(first, last + 1, 2)) + [last]:
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    pts, polys = world_points(body)
    tree, seat_tree, rest_polys = split_trees(pts, polys)
    total = 0
    struck = set()
    for part in pods + rounds:
        ppts, _ = world_points(part)
        for p in ppts:
            if inside_count([p], tree):
                total += 1
                index = tree.find_nearest(p)[2]
                if index is not None:
                    struck.add(owner[rest_polys[index][0]])
    worst = max(worst, total)
    print("ROCKETS_FRAME %d: %d launcher and rocket vertices inside other plates%s" % (frame, total, (" (" + ", ".join(sorted(struck)) + ")") if struck else ""))
print(("ROCKETS_CLEAR" if worst == 0 else "ROCKETS_HIT") + " worst %d vertices inside other plates over the open clip" % worst)

# How the launcher sits in its seat, shut: the share of it sunk into the armour, how far its underside is from the armour, and
# whether every tube's mouth sees out forward (a rocket's first 2 m clear of the whole suit).
scene.frame_set(first)
bpy.context.view_layer.update()
pts, polys = world_points(body)
_, seat_tree, _ = split_trees(pts, polys)
whole = BVHTree.FromPolygons(pts, polys)
for i, pod in enumerate(pods):
    ppts, _ = world_points(pod)
    sunk = sum(inside_count([p], seat_tree) for p in ppts)
    low = min(p.z for p in ppts)
    gaps = sorted(seat_tree.find_nearest(p)[3] for p in ppts if p.z < low + 0.015 and not inside_count([p], seat_tree))
    print("ROCKETS_SEAT launcher on %s: %d%% sunk into the armour; underside to armour median %.1f cm, worst %.1f cm" % (
        carriers[i], round(100 * sunk / len(ppts)), (gaps[len(gaps) // 2] if gaps else 0) * 100, (gaps[-1] if gaps else 0) * 100))
for socket, holder in muzzles:
    start = socket.matrix_world.to_translation()
    forward = (holder.matrix_world.to_3x3() @ Vector((0, -1, 0))).normalized()
    hit = whole.ray_cast(start + forward * 0.005, forward, 2.0)
    print("ROCKETS_MUZZLE %s: %s" % (socket.name.split(".")[0], "clear for 2 m" if hit[0] is None else "blocked by the suit %.2f m out" % hit[3]))

# Renders.
scene.render.engine = "BLENDER_WORKBENCH"
shading = scene.display.shading
shading.light = "STUDIO"
shading.color_type = "TEXTURE"
shading.show_shadows = True
shading.show_cavity = True
shading.cavity_type = "BOTH"
scene.render.resolution_x, scene.render.resolution_y = 1400, 1400
scene.render.film_transparent = False
world = bpy.data.worlds.new("w") if scene.world is None else scene.world
scene.world = world
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
scene.collection.objects.link(cam)
scene.camera = cam


def shoot(name, direction, target, distance, lens=50):
    d = Vector(direction).normalized()
    cam.data.lens = lens
    cam.location = target + d * distance
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = os.path.join(out, name + ".png")
    bpy.ops.render.render(write_still=True)


upper = Vector((0, 0.0, 1.7))
for frame, state in ((first, "shut"), (last, "open")):
    scene.frame_set(frame)
    if state == "shut":
        shoot("%s_rockets_shut_front" % prefab, (0, -1, 0.2), upper, 2.4)
    shoot("%s_rockets_%s_back" % (prefab, state), (0, 1, 0.25), upper, 2.4)
    shoot("%s_rockets_%s_rear" % (prefab, state), (0.75, 0.8, 0.35), upper, 2.4)

# Size: the shut suit's head and shoulders from the front, square, with the launcher, the helmet and the launcher's plate
# outlined and measured (width x height x depth, metres).
scene.frame_set(first)
bpy.context.view_layer.update()


def extent(points):
    lo = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    hi = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    return lo, hi


pts, _ = world_points(body)
front = min(p.y for p in pts) - 0.05  # outlines and labels in front of the whole suit
font_path = "C:/Windows/Fonts/msyh.ttc"
font = bpy.data.fonts.load(font_path) if os.path.exists(font_path) else None


def flat(name, colour):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = colour
    mat.use_nodes = True
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = colour
    return mat


def outline(label, lo, hi, colour, text_at, align):
    mat = flat(label, colour)
    t = 0.004
    y = front
    edges = (((lo.x, lo.z), (hi.x, lo.z)), ((lo.x, hi.z), (hi.x, hi.z)), ((lo.x, lo.z), (lo.x, hi.z)), ((hi.x, lo.z), (hi.x, hi.z)))
    for a, b in edges:
        bpy.ops.mesh.primitive_cube_add(size=1)
        bar = bpy.context.active_object
        bar.location = ((a[0] + b[0]) / 2, y, (a[1] + b[1]) / 2)
        bar.scale = (max(abs(b[0] - a[0]), t), t, max(abs(b[1] - a[1]), t))
        bar.data.materials.append(mat)
    size = hi - lo
    curve = bpy.data.curves.new(label + ".text", "FONT")
    curve.body = "%s\n%.2f × %.2f × %.2f m" % (label, size.x, size.z, size.y)
    curve.size = 0.032
    curve.align_x = align
    if font:
        curve.font = font
    text = bpy.data.objects.new(label + ".text", curve)
    scene.collection.objects.link(text)
    text.location = (text_at[0], y, text_at[1])
    text.rotation_euler = (math.pi / 2, 0, 0)
    text.data.materials.append(mat)


# The shoulder plate measured beside the launcher: the optional last argument, else the plate the launcher rides.
plate = [pts[i] for i in range(len(pts)) if owner[i] == (shoulder[0] if shoulder else carriers[0])]
helmet = [pts[i] for i in range(len(pts)) if owner[i] == "head"]
pod_lo, pod_hi = extent(world_points(pods[0])[0])
plate_lo, plate_hi = extent(plate)
helmet_lo, helmet_hi = extent(helmet)
outline("火箭巢", pod_lo, pod_hi, (1.0, 0.55, 0.05, 1), (pod_lo.x, pod_hi.z + 0.07), "LEFT")
outline("肩甲", plate_lo, plate_hi, (0.1, 0.8, 1.0, 1), (plate_lo.x, plate_lo.z - 0.08), "LEFT")
outline("头盔", helmet_lo, helmet_hi, (0.4, 1.0, 0.3, 1), (0, helmet_hi.z + 0.07), "CENTER")
print("ROCKETS_SIZE launcher %s, helmet %s, plate %s (width x depth x height, m)" % (
    tuple(round(c, 3) for c in pod_hi - pod_lo), tuple(round(c, 3) for c in helmet_hi - helmet_lo), tuple(round(c, 3) for c in plate_hi - plate_lo)))
scene.render.resolution_x, scene.render.resolution_y = 1600, 1200
cam.data.type = "ORTHO"
cam.data.ortho_scale = 1.5
cam.location = Vector((0, -5, 1.84))
cam.rotation_euler = Vector((0, 1, 0)).to_track_quat("-Z", "Y").to_euler()
scene.render.filepath = os.path.join(out, "%s_rockets_size.png" % prefab)
bpy.ops.render.render(write_still=True)
print("ROCKETS_RENDERED " + out)
