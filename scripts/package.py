"""Build REDLINE and package it with a skin map generated from a local Steam installation."""

import argparse
import re
import subprocess
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


ROOT = Path(__file__).resolve().parent.parent
DEFAULT_GAME_DIR = Path(r"C:\Program Files (x86)\Steam\steamapps\common\SIGNALIS")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, default=DEFAULT_GAME_DIR, help="directory containing SIGNALIS.exe")
    args = parser.parse_args()
    game = args.game_dir.resolve()
    if not (game / "SIGNALIS.exe").is_file():
        parser.error(f"SIGNALIS.exe is missing from {game}")
    if not (game / "MelonLoader/Managed/Assembly-CSharp.dll").is_file():
        parser.error(f"MelonLoader's generated managed assemblies are missing from {game}")

    source = ROOT / "Source"
    mod_info = (source / "RedlineMod.cs").read_text(encoding="utf-8-sig")
    match = re.search(r'MelonInfo\(typeof\(Redline\.RedlineMod\), "REDLINE", "([0-9]+\.[0-9]+\.[0-9]+)"', mod_info)
    if not match:
        raise ValueError("Could not read the version from REDLINE's MelonInfo attribute")
    version = match.group(1)

    subprocess.run(
        ["dotnet", "build", str(source / "Redline.csproj"), "-c", "Release", f"-p:GameDir={game}"],
        check=True,
    )
    dll = source / "bin/Release/net472/Redline.dll"
    skin_map = ROOT / "build/RedlineSkinMap.gz"
    subprocess.run([sys.executable, str(source / "make_skin_map.py"), str(game), str(skin_map)], check=True)

    package = ROOT / "dist" / f"REDLINE-{version}.zip"
    package.parent.mkdir(exist_ok=True)
    with ZipFile(package, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
        archive.write(dll, "Mods/Redline.dll")
        archive.write(skin_map, "UserData/RedlineSkinMap.gz")
        for file in ("LICENSE", "README.md", "requirements.txt", "UserData/Redline.cfg", "scripts/package.py"):
            archive.write(ROOT / file, file)
        for file in sorted(source.iterdir()):
            if file.is_file() and file.suffix in {".cs", ".csproj", ".py"}:
                archive.write(file, file.relative_to(ROOT).as_posix())

    print(f"Created {package}")


if __name__ == "__main__":
    main()
