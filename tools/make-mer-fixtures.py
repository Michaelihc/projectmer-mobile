"""Generates ProjectMER test fixtures (docs/projectmer-port-plan.md section 5.3) into .runtime/mer-fixtures.

Output:
  .runtime/mer-fixtures/Schematics/<name>/<name>.json   synthetic schematics (+ copies of real ones with --real)
  .runtime/mer-fixtures/Maps/<name>.yml                 maps that place them, plus an every-object-type map

Usage:
  python tools/make-mer-fixtures.py [--real <ProjectMER schematics dir>] [--out <dir>]

Real schematics are copied, never modified. Fixtures are not committed.
"""

import argparse
import json
import math
import os
import shutil

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_REAL = (r"<scpsl-plugins-metarepo>\.tests\offline-clients\runtime"
                r"\baselines\oa1\318d7a87fbb96fecedd38834f25eaac0c50295a2c66cfe7df1afc10df79b90ab\configs\8910\AutoEvent"
                r"\Schematics\ProjectMER")
REAL_NAMES = ["35Hp", "Battle", "DeathParty", "Shipment", "Skeld", "Jail"]

# Surface, away from the spawn buildings; maps use world coordinates (room "Unknown" = Outside).
ORIGIN = (20.0, 1003.0, -60.0)

EMPTY, PRIMITIVE, LIGHT, PICKUP, WORKSTATION, SCHEMATIC, TELEPORT, LOCKER, TEXT, INTERACTABLE, WAYPOINT, TRIANGLE = range(12)
SPHERE, CAPSULE, CYLINDER, CUBE, PLANE, QUAD = range(6)


def v(x=0.0, y=0.0, z=0.0):
    return {"x": float(x), "y": float(y), "z": float(z)}


class Schematic:
    def __init__(self, root_id=1000):
        self.root = root_id
        self.blocks = []
        self.next_id = root_id + 1

    def add(self, name, block_type, parent=None, pos=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1), props=None, animator=None):
        oid = self.next_id
        self.next_id += 1
        self.blocks.append({
            "Name": name,
            "ObjectId": oid,
            "ParentId": self.root if parent is None else parent,
            "AnimatorName": animator,
            "Position": v(*pos),
            "Rotation": v(*rot),
            "Scale": v(*scale),
            "BlockType": block_type,
            "Properties": props or {},
        })
        return oid

    def cube(self, name, parent=None, pos=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1), color="#808080", flags=3, ptype=CUBE, static=None):
        props = {"PrimitiveType": ptype, "Color": color, "PrimitiveFlags": flags}
        if static is not None:
            props["Static"] = static
        return self.add(name, PRIMITIVE, parent, pos, rot, scale, props)

    def write(self, directory, name):
        path = os.path.join(directory, name)
        os.makedirs(path, exist_ok=True)
        with open(os.path.join(path, name + ".json"), "w", encoding="utf-8") as f:
            json.dump({"RootObjectId": self.root, "Blocks": self.blocks}, f, indent=1)


def grid(count, static):
    s = Schematic()
    side = math.ceil(math.sqrt(count))
    for i in range(count):
        x, z = i % side, i // side
        hue = (i * 37) % 255
        s.cube(f"Cube{i}", pos=(x * 1.1, 0, z * 1.1), color=f"#{hue:02X}80{255 - hue:02X}", static=None if static else False)
    return s


def flag_matrix():
    s = Schematic()
    scales = [(1, 1, 1), (-1, 1, 1), (1, -1, 1), (-1, -1, -1)]
    i = 0
    for flags in range(4):
        for ptype in range(6):
            for k, sc in enumerate(scales):
                s.cube(f"F{flags}_T{ptype}_S{k}", pos=(ptype * 2.0, flags * 2.0, k * 2.0), rot=(0, 30 * k, 0),
                       scale=sc, color="#40C0FF", flags=flags, ptype=ptype)
                i += 1
    # Legacy block without PrimitiveFlags: negative Scale.x means not collidable.
    s.add("LegacyNoFlags", PRIMITIVE, pos=(-3, 0, 0), scale=(-1, 1, 1), props={"PrimitiveType": CUBE, "Color": "#FF00FF"})
    # Zero alpha, not collidable: dropped by the optimizer.
    s.cube("AlphaZeroVisible", pos=(-3, 2, 0), color="#FF000000", flags=2)
    # Zero scale: dropped by the optimizer.
    s.cube("ZeroScale", pos=(-3, 4, 0), scale=(0, 0, 0))
    return s


def nested_empties(static=True):
    s = Schematic()
    parent = None
    rots = [(0, 30, 0), (45, 0, 10), (0, 0, 60), (20, 70, 0), (10, 10, 10)]
    scales = [(2, 1, 1), (1, 0.5, 1.5), (0.7, 2, 1), (1, 1, 3), (1.5, 0.5, 0.8)]
    for depth in range(5):
        parent = s.add(f"Empty{depth}", EMPTY, parent, pos=(1, 0.5, 0), rot=rots[depth], scale=scales[depth])
        s.cube(f"Leaf{depth}", parent, pos=(0.5, 0.25, -0.5), rot=(15 * depth, 0, 5), scale=(0.5, 0.25, 0.75),
               color="#FFC040", static=None if static else False)
        s.cube(f"LeafNeg{depth}", parent, pos=(-0.5, 0, 0.5), rot=(0, 25, 0), scale=(-0.4, 0.3, 0.2), color="#40FF40", flags=2)
    # A primitive with children: needs an anchor and a toy.
    holder = s.cube("PrimitiveWithChildren", pos=(-2, 0, 0), rot=(0, 45, 0), scale=(1, 2, 1), color="#C0C0C0")
    s.cube("ChildOfPrimitive", holder, pos=(0, 0.75, 0), scale=(0.5, 0.25, 0.5), color="#202020")
    return s


def lights(count, with_spot):
    s = Schematic()
    for i in range(count):
        s.add(f"Light{i}", LIGHT, pos=(i * 2.0, 2, 0), props={"Color": "#FFE0C0", "Intensity": 1.0 + i, "Range": 5.0 + i,
                                                             "ShadowType": 2, "LightType": 2, "Shape": 0,
                                                             "SpotAngle": 30.0, "InnerSpotAngle": 0.0, "ShadowStrength": 1.0})
    if with_spot:
        s.add("Spot", LIGHT, pos=(0, 3, 3), rot=(90, 0, 0), props={"Color": "#FFFFFF", "Intensity": 3.0, "Range": 40.0,
                                                                   "ShadowType": 1, "LightType": 0, "Shape": 0,
                                                                   "SpotAngle": 45.0, "InnerSpotAngle": 10.0, "ShadowStrength": 1.0})
    s.cube("Floor", pos=(count, 0, 0), scale=(count * 2 + 2, 0.1, 6))
    return s


def legacy_13():
    s = Schematic()
    s.add("Wall", PRIMITIVE, pos=(0, 1, 0), scale=(4, 2, 0.1), props={"PrimitiveType": CUBE, "Color": "#AAAAAA"})
    s.add("Ghost", PRIMITIVE, pos=(0, 1, 1), scale=(-1, 1, 1), props={"PrimitiveType": SPHERE, "Color": "#FFFFFF80"})
    s.add("OldLight", LIGHT, pos=(0, 2.5, 0), props={"Color": "#FFFFFF", "Intensity": 2.0, "Range": 8.0, "Shadows": True})
    s.add("Card", PICKUP, pos=(1, 0.5, 0), props={"ItemType": "KeycardMTFPrivate"})
    s.add("Medkit", PICKUP, pos=(1.5, 0.5, 0), props={"ItemType": "Medkit", "Locked": True})
    s.add("NumberedRadio", PICKUP, pos=(2, 0.5, 0), props={"ItemType": 12})
    s.add("NewGun", PICKUP, pos=(2.5, 0.5, 0), props={"ItemType": "GunA7"})
    s.add("Bench", WORKSTATION, pos=(-2, 0, 0), rot=(0, 90, 0), props={"IsInteractable": True})
    return s


def unsupported_blocks():
    s = Schematic()
    s.cube("Base", pos=(0, 0, 0), scale=(4, 0.2, 4))
    t = s.add("Sign", TEXT, pos=(0, 1, 0), props={"Text": "Hello", "DisplaySize": {"x": 10, "y": 2}})
    s.cube("ChildOfText", t, pos=(0, 0.5, 0), scale=(0.2, 0.2, 0.2))
    s.add("Button", INTERACTABLE, pos=(1, 1, 0), props={"Shape": 0, "InteractionDuration": 1.0})
    s.add("Path", WAYPOINT, pos=(0, 0, 0), scale=(4, 4, 4))
    s.add("Tri", TRIANGLE, pos=(0, 2, 0), props={"PointA": {"x": 0, "y": 0, "z": 0}, "PointB": {"x": 1, "y": 0, "z": 0},
                                               "PointC": {"x": 0, "y": 1, "z": 0}, "Color": "#FF0000"})
    sub = s.add("SubSchematic", SCHEMATIC, pos=(2, 0, 0))
    s.cube("HiddenUnderSubSchematic", sub)
    s.add("TeleportBlock", TELEPORT, pos=(-2, 0, 0))
    s.add("LockerBlock", LOCKER, pos=(-2, 0, 2))
    s.add("Marker", EMPTY, pos=(0, 3, 0))
    return s


def physics():
    s = Schematic()
    s.cube("Ground", pos=(0, 0, 0), scale=(6, 0.2, 6))
    box = s.cube("FallingBox", pos=(0, 3, 0), scale=(0.5, 0.5, 0.5), color="#FF8000")
    s.cube("Antenna", box, pos=(0, 1, 0), scale=(0.1, 1, 0.1), color="#000000")
    return s, {str(box): {"IsKinematic": False, "UseGravity": True, "Constraints": 0, "Mass": 1.0}}


def yaml_vec(x, y, z):
    return f"{x:.3f}, {y:.3f}, {z:.3f}"


def map_with_schematic(schematic, offset=(0, 0, 0)):
    x, y, z = (ORIGIN[0] + offset[0], ORIGIN[1] + offset[1], ORIGIN[2] + offset[2])
    return f"""schematics:
  {schematic.lower()}:
    schematic_name: {schematic}
    position: {yaml_vec(x, y, z)}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
"""


EVERY_TYPE_MAP = """primitives:
  prim_visible_collidable:
    primitive_type: Cube
    color: '#FF0000'
    primitive_flags: Collidable, Visible
    position: {p0}
    rotation: 0.000, 30.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  prim_visible_only_negative:
    primitive_type: Quad
    color: '#00FF00'
    primitive_flags: Visible
    position: {p1}
    rotation: 0.000, 0.000, 0.000
    scale: -1.000, -1.000, -1.000
    room: Unknown
    index: -1
  prim_collidable_only:
    primitive_type: Cube
    color: '#0000FF'
    primitive_flags: Collidable
    position: {p2}
    rotation: 0.000, 0.000, 0.000
    scale: 2.000, 2.000, 0.100
    room: Unknown
    index: -1
  prim_none:
    primitive_type: Sphere
    color: '#FFFFFF'
    primitive_flags: None
    position: {p3}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  prim_unknown_room:
    primitive_type: Cube
    color: '#FFFFFF'
    primitive_flags: Collidable, Visible
    position: 0.000, 1.000, 0.000
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: HeavyContainment_Straight_Hcz127
    index: -1
triangles:
  tri1:
    point_a: -0.500, -0.500, 0.000
    point_b: 0.500, -0.500, 0.000
    point_c: -0.500, 0.500, 0.000
    color: '#FF0000'
    thickness: 0.01
    position: {p4}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
lights:
  light_hard_shadows:
    color: '#FFFFFF'
    intensity: 2
    range: 10
    shadows: Hard
    strength: 1
    light_type: Point
    shape: Cone
    spot_angle: 30
    inner_spot_angle: 0
    position: {p5}
    rotation: 0.000, 0.000, 0.000
    room: Unknown
    index: -1
  light_spot:
    color: red
    intensity: 5
    range: 45
    shadows: None
    strength: 0
    light_type: Spot
    shape: Cone
    spot_angle: 60
    inner_spot_angle: 10
    position: {p6}
    rotation: 90.000, 0.000, 0.000
    room: Unknown
    index: -1
doors:
  door_lcz:
    door_type: Lcz
    is_open: true
    is_locked: false
    required_permissions: Checkpoints, ContainmentLevelOne
    require_all: true
    position: {p7}
    rotation: 0.000, 90.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  door_ez_all:
    door_type: Ez
    is_open: false
    is_locked: true
    required_permissions: All
    require_all: false
    position: {p8}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  door_bulk:
    door_type: Bulkdoor
    is_open: false
    is_locked: false
    required_permissions: None
    require_all: true
    position: {p9}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  door_gate:
    door_type: Gate
    is_open: false
    is_locked: false
    required_permissions: None
    require_all: true
    position: {p10}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
workstations:
  ws1:
    is_interactable: true
    position: {p11}
    rotation: 0.000, 45.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
item_spawnpoints:
  isp_lantern:
    item_type: Lantern
    weight: -1
    attachments_code: -1
    number_of_items: 1
    number_of_uses: 1
    use_gravity: true
    can_be_picked_up: true
    position: {p12}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  isp_mtf_card:
    item_type: KeycardMTFCaptain
    weight: -1
    attachments_code: -1
    number_of_items: 2
    number_of_uses: 3
    use_gravity: false
    can_be_picked_up: true
    position: {p13}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  isp_gun:
    item_type: GunE11SR
    weight: -1
    attachments_code: -1
    number_of_items: 1
    number_of_uses: 1
    use_gravity: true
    can_be_picked_up: true
    position: {p14}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  isp_unknown_item:
    item_type: GunA7
    weight: -1
    attachments_code: -1
    number_of_items: 1
    number_of_uses: 1
    use_gravity: true
    can_be_picked_up: true
    position: {p15}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
player_spawnpoints:
  psp1:
    roles:
    - ClassD
    - Flamingo
    - Scientist
    position: {p16}
    rotation: 0.000, 0.000, 0.000
    room: Unknown
    index: -1
capybaras:
  capy1:
    position: {p17}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
texts:
  text1:
    text: Hello <b>world</b>
    display_size: 200.000, 50.000, 0.000
    position: {p18}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
interactables:
  inter1:
    collider_shape: Sphere
    interaction_duration: 2.5
    is_locked: true
    position: {p19}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
scp079_cameras:
  cam1:
    camera_type: EzArm
    label: CustomCam
    position: {p20}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
shooting_targets:
  target_sport:
    target_type: Sport
    position: {p21}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  target_dboy:
    target_type: ClassD
    position: {p22}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  target_binary:
    target_type: Binary
    position: {p23}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
schematics:
  schem_existing:
    schematic_name: DeathParty
    position: {p24}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  schem_missing:
    schematic_name: DoesNotExist
    position: {p25}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
teleports:
  tp1:
    targets:
    - tp2
    cooldown: 5
    position: {p26}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 2.000, 1.000
    room: Unknown
    index: -1
  tp2:
    targets:
    - tp1
    cooldown: 5
    position: {p27}
    rotation: 0.000, 180.000, 0.000
    scale: 1.000, 2.000, 1.000
    room: Unknown
    index: -1
lockers:
  locker_medkit:
    locker_type: Medkit
    loot:
    - target_item: Medkit
      remaining_uses: 1
      max_per_chamber: 1
      probability_points: 100
      min_per_chamber: 1
    chambers:
    - acceptable_items:
      - Medkit
      is_open: false
      required_permissions: None
    position: {p28}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
  locker_scp1344:
    locker_type: PedestalScp1344
    loot: []
    chambers: []
    position: {p29}
    rotation: 0.000, 0.000, 0.000
    scale: 1.000, 1.000, 1.000
    room: Unknown
    index: -1
waypoints:
  wp1:
    position: {p30}
    rotation: 0.000, 0.000, 0.000
    scale: 4.000, 4.000, 4.000
    room: Unknown
    index: -1
"""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--real", default=DEFAULT_REAL)
    parser.add_argument("--out", default=os.path.join(REPO, ".runtime", "mer-fixtures"))
    args = parser.parse_args()

    schematics = os.path.join(args.out, "Schematics")
    maps = os.path.join(args.out, "Maps")
    if os.path.isdir(args.out):
        shutil.rmtree(args.out)
    os.makedirs(schematics)
    os.makedirs(maps)

    synthetic = {}
    for n in (100, 500, 2000):
        synthetic[f"Grid{n}Static"] = grid(n, True)
        synthetic[f"Grid{n}Dynamic"] = grid(n, False)
    synthetic["FlagMatrix"] = flag_matrix()
    synthetic["NestedEmpties"] = nested_empties()
    synthetic["Lights4"] = lights(4, False)
    synthetic["Lights16Spot"] = lights(16, True)
    synthetic["Legacy13"] = legacy_13()
    synthetic["UnsupportedBlocks"] = unsupported_blocks()
    phys, rigidbodies = physics()
    synthetic["Physics"] = phys

    for name, s in synthetic.items():
        s.write(schematics, name)
        with open(os.path.join(maps, f"Map{name}.yml"), "w", encoding="utf-8") as f:
            f.write(map_with_schematic(name))

    with open(os.path.join(schematics, "Physics", "Physics-Rigidbodies.json"), "w", encoding="utf-8") as f:
        json.dump(rigidbodies, f, indent=1)

    copied = []
    for name in REAL_NAMES:
        source = os.path.join(args.real, name)
        if os.path.isdir(source):
            shutil.copytree(source, os.path.join(schematics, name))
            with open(os.path.join(maps, f"Map{name}.yml"), "w", encoding="utf-8") as f:
                f.write(map_with_schematic(name))
            copied.append(name)

    positions = {}
    for i in range(31):
        positions[f"p{i}"] = yaml_vec(ORIGIN[0] + (i % 8) * 3.0, ORIGIN[1], ORIGIN[2] - 20 - (i // 8) * 3.0)
    with open(os.path.join(maps, "EveryType.yml"), "w", encoding="utf-8") as f:
        f.write(EVERY_TYPE_MAP.format(**positions))

    print(f"Wrote {len(synthetic)} synthetic schematics, copied {copied}, maps: {sorted(os.listdir(maps))}")


if __name__ == "__main__":
    main()
