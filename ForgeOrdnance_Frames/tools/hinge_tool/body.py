"""The suit's body from its joints (numpy only): body-part axes, which part a set of points sits on, a capsule round each
part for the penetration check, and the pilot. Only the exosuit presets and pilot checks use this; a model without joints
(the scene property "frame") skips them."""
import json

import numpy as np

LIMBS = (("upperarm", "forearm"), ("forearm", "hand"), ("thigh", "shin"), ("shin", "foot"))
# Typical radius of each body part (m) for telling which one a point lies on.
REACH_R = {"back": 0.15, "upperarm": 0.06, "forearm": 0.05, "thigh": 0.085, "shin": 0.06}
# The game's pilot (half width, m), slimmer than a reference body; the preview draws it at PILOT_SCALE.
PILOT = {"torso": 0.136, "upperarm": 0.036, "forearm": 0.032, "thigh": 0.052, "shin": 0.04}
PILOT_SCALE = 0.8


def joints(scene):
    """{name: np.array} from the scene's custom property "frame", or None."""
    try:
        meta = json.loads(scene.get("frame", "{}"))
    except (TypeError, ValueError):
        return None
    j = meta.get("joints")
    return {k: np.array(v, float) for k, v in j.items()} if j else None


def segments(j):
    """[(name, side, head, tail)] of the torso ("back", from 0.2 m below the pelvis to the neck) and the limb segments."""
    out = [("back", None, j["pelvis"] - np.array([0, 0, 0.2]), j["neck"])]
    for s in ("L", "R"):
        for limb, end in LIMBS:
            out.append((limb, s, j[limb + "." + s], j[end + "." + s]))
    return out


def from_axis(head, tail, pts):
    """(distance from the segment head-tail, unit direction out from it) for each point."""
    d = tail - head
    v = pts - (head + np.outer(np.clip(((pts - head) @ d) / (d @ d), 0, 1), d))
    n = np.linalg.norm(v, axis=1)
    return n, v / np.maximum(n, 1e-9)[:, None]


def region(j, pts):
    """Where points sit (region, side, lift): each point votes for the body part it is nearest (measured from its surface; a
    limb claims only what lies past its joint, and the upper arm and thigh nothing past the elbow or knee, which the next part
    claims); the torso or the arm or leg with most votes wins, then its part with most; the side of their centre (L is +X),
    and whether it is a part over the shoulders."""
    pts = np.asarray(pts, float)
    sample = pts[::max(1, len(pts) // 400)]
    segs = segments(j)
    nxt = dict(LIMBS[0::2])        # upperarm -> forearm, thigh -> shin
    cost = []
    for name, side, h, t in segs:
        dist = from_axis(h, t, sample)[0] - REACH_R[name]
        if side is not None:
            dist[(sample - h) @ (t - h) <= 0] = np.inf
            if name in nxt:
                h2, t2 = j[nxt[name] + "." + side], j[dict(LIMBS)[nxt[name]] + "." + side]
                dist[(sample - h2) @ (t2 - h2) > 0] = np.inf
        cost.append(dist)
    votes = np.bincount(np.argmin(np.stack(cost, axis=1), axis=1), minlength=len(segs))
    # first the torso or which arm or leg (its two parts' votes together), then the part of it most points are nearest
    chain = [(s[1], s[0] in ("thigh", "shin")) if s[1] else None for s in segs]
    group = max(dict.fromkeys(chain), key=lambda c: sum(v for v, x in zip(votes, chain) if x == c))
    k = max((i for i, x in enumerate(chain) if x == group), key=lambda i: votes[i])
    name, side = segs[k][:2]
    c = pts.mean(axis=0)
    if side is None:
        side = "L" if c[0] > 0.03 else "R" if c[0] < -0.03 else "C"
    return name, side, name == "back" and c[2] > j["neck"][2] + 0.05


def seat(j, reg, side):
    """The axis segment of the body part a piece sits on."""
    if reg == "back":
        return j["pelvis"] - np.array([0, 0, 0.2]), j["neck"]
    end = dict(LIMBS)[reg]
    return j[reg + "." + side], j[end + "." + side]


class Capsules:
    """The body inside the suit: a capsule round each body part's axis (the torso from the pelvis up, ending flat there), its
    radius the suit's innermost layer at it (the 10th percentile distance of the suit's points nearest that axis)."""

    def __init__(self, j, suit_pts):
        self.j = j
        self.caps = [("torso", j["pelvis"], j["neck"])]
        for s in ("L", "R"):
            for limb, end in LIMBS:
                self.caps.append((limb + "." + s, j[limb + "." + s], j[end + "." + s]))
        d = np.stack([from_axis(h, t, suit_pts)[0] for _, h, t in self.caps], axis=1)
        near = d.argmin(axis=1)
        self.radii = np.array([np.quantile(d[near == k, k], 0.10) if (near == k).any() else 0.0
                               for k in range(len(self.caps))])

    def depth(self, pts):
        """(how far each point lies inside the deepest capsule, index of that capsule)."""
        d = np.stack([from_axis(h, t, pts)[0] for _, h, t in self.caps], axis=1)
        below = (pts - self.j["pelvis"]) @ (self.j["neck"] - self.j["pelvis"]) < 0
        d[below, 0] = np.inf
        inside = self.radii[None, :] - d
        return np.clip(inside.max(axis=1), 0, None), inside.argmax(axis=1)

    def name(self, k):
        return self.caps[k][0]

    def foot(self, pts, which):
        """The point on capsule `which[i]`'s axis nearest each point."""
        out = np.empty_like(pts)
        for k in set(which.tolist()):
            sel = which == k
            _, h, t = self.caps[k]
            d = t - h
            s = np.clip(((pts[sel] - h) @ d) / (d @ d), 0, 1)
            out[sel] = h + np.outer(s, d)
        return out


def angles(head, tail, pts):
    """Angles (degrees, 0 straight back) of points about a limb axis."""
    ax = (tail - head) / np.linalg.norm(tail - head)
    w = np.array([0, 1, 0]) - ax[1] * ax
    w /= np.linalg.norm(w)
    u = np.cross(ax, w)
    r = pts - head
    return np.degrees(np.arctan2(r @ u, r @ w)) % 360


def pilot(j):
    """The pilot's capsules [(head, tail, radius)], drawn PILOT_SCALE thin along the suit's own body parts."""
    out = [(j["pelvis"], j["neck"], PILOT["torso"] * PILOT_SCALE)]
    for s in ("L", "R"):
        for limb, end in LIMBS:
            out.append((j[limb + "." + s], j[end + "." + s], PILOT[limb] * PILOT_SCALE))
    return out
