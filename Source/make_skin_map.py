"""Write limb labels and welded bind-pose vertex indices for unreadable meshes."""

import gzip
import struct
import sys
from pathlib import Path

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler


ROOTS = {"shin_l": 1, "shin_r": 2, "upper_arm_l": 3, "upper_arm_r": 4, "head": 5}


def label(ptr):
    while ptr:
        bone = ptr.read()
        name = bone.m_GameObject.read().m_Name.lower()
        if name in ROOTS:
            return ROOTS[name]
        ptr = bone.m_Father
    return 0


def main(game, output):
    env = UnityPy.load(str(game / "SIGNALIS_Data" / "data.unity3d"))
    lookup = {}
    visited = set()
    for obj in env.objects:
        if obj.type.name != "SkinnedMeshRenderer":
            continue
        renderer = obj.read()
        if not renderer.m_Mesh or not renderer.m_Bones:
            continue
        reader = renderer.m_Mesh.deref()
        # The Kolibri body has the same limb roots on a compact 21-bone rig.
        if len(renderer.m_Bones) < 40 and reader.peek_name() != "KLBR_Body":
            continue
        identity = (reader.assets_file.name, reader.path_id)
        if identity in visited:
            continue
        visited.add(identity)
        source = reader.read()
        names = {ptr.read().m_GameObject.read().m_Name.lower() for ptr in renderer.m_Bones if ptr}
        if len(names.intersection(ROOTS)) < 3:
            continue

        mesh = MeshHandler(source)
        mesh.process()
        if not mesh.m_BoneIndices or not mesh.m_BoneWeights:
            continue
        categories = [label(ptr) for ptr in renderer.m_Bones]
        values = bytearray()
        for joints, weights in zip(mesh.m_BoneIndices, mesh.m_BoneWeights):
            scores = [0.0] * 6
            for joint, weight in zip(joints, weights):
                if 0 <= joint < len(categories):
                    scores[categories[joint]] += weight
            values.append(max(range(6), key=scores.__getitem__))
        key = (source.m_Name, len(values), len(source.m_SubMeshes))
        unique = {}
        welded = [unique.setdefault(tuple(round(axis * 10000) for axis in vertex[:3]), index)
                  for index, vertex in enumerate(mesh.m_Vertices)]
        if len(welded) == len(values):
            lookup.setdefault(key, (values, welded))

    if not lookup:
        raise RuntimeError("No compatible skin bindings were found")
    output.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(output, "wb") as data:
        data.write(struct.pack("<4sI", b"RL02", len(lookup)))
        for (name, count, submeshes), (values, welded) in sorted(lookup.items()):
            name = name.encode("utf-8")
            data.write(struct.pack("<III", len(name), count, submeshes))
            data.write(name)
            data.write(values)
            data.write(struct.pack(f"<{count}i", *welded))
    print(f"Mapped {len(lookup)} locally installed meshes to limb labels")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
