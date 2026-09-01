from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


PNG_SIZES = (16, 20, 24, 32, 40, 44, 48, 64, 72, 96, 128, 150, 256, 310, 512)
ICO_SIZES = ((16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (96, 96), (128, 128), (256, 256))


def resized(source: Image.Image, size: int) -> Image.Image:
    return source.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate the NaxUpdater Windows icon family.")
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    with Image.open(args.source) as image:
        master = image.convert("RGBA")
        master.save(args.output / "AppIcon-Master.png", optimize=True)
        for size in PNG_SIZES:
            resized(master, size).save(args.output / f"AppIcon-{size}.png", optimize=True)

        master.save(args.output / "AppIcon.ico", format="ICO", sizes=ICO_SIZES)
        # WiX 4 uses the classic Win32 resource updater for Burn's window icon.
        # Keep a BMP-backed, pre-Vista-compatible icon without the 256px PNG frame.
        master.save(
            args.output / "AppIcon-Installer.ico",
            format="ICO",
            sizes=((16, 16), (24, 24), (32, 32), (40, 40), (48, 48)),
            bitmap_format="bmp",
        )

        aliases = {
            "Square44x44Logo.scale-100.png": 44,
            "Square44x44Logo.scale-200.png": 88,
            "Square44x44Logo.targetsize-24_altform-unplated.png": 24,
            "Square44x44Logo.targetsize-48_altform-unplated.png": 48,
            "Square150x150Logo.scale-100.png": 150,
            "Square150x150Logo.scale-200.png": 300,
            "StoreLogo.png": 50,
        }
        for name, size in aliases.items():
            resized(master, size).save(args.output / name, optimize=True)


if __name__ == "__main__":
    main()
