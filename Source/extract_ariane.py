"""Extract the Ariane flash art pool from a local SIGNALIS installation."""

import argparse
from pathlib import Path

import UnityPy


FRAMES = (
    "PEN_Nach_33",
    "END_Ariane_Falke",
    "MEM_Promise_3",
    "END_Death_11",
    "END_Circle_4",
    "ROT_Tooth_2",
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path, help="directory containing SIGNALIS.exe")
    game = parser.parse_args().game.resolve()
    bundle = game / "SIGNALIS_Data/data.unity3d"
    if not bundle.is_file():
        parser.error(f"Game data is missing from {bundle}")

    assets = UnityPy.load(str(bundle))
    pending = set(FRAMES)
    images = {}
    for obj in assets.objects:
        if obj.type.name != "Texture2D":
            continue
        name = obj.peek_name()
        if name in pending:
            images[name] = obj.read().image
            pending.remove(name)
            if not pending:
                break
    if pending:
        parser.error(f"Ariane images missing from {bundle}: {', '.join(sorted(pending))}")

    output = game / "UserData/RedlineAriane"
    output.mkdir(parents=True, exist_ok=True)
    for name in FRAMES:
        images[name].save(output / f"{name}.png")
    print(f"Extracted {len(images)} Ariane images to {output}")


if __name__ == "__main__":
    main()
