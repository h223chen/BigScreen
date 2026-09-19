"""Generate the 256x256 PNG icon Thunderstore requires.

Thunderstore rejects packages whose icon is not exactly 256x256, so this makes a
simple placeholder (a screen on legs with a play triangle). Replace with real art
whenever you have some: just drop your own thunderstore/icon.png in place.

usage: python make-icon.py <out.png>
requires: pip install pillow
"""
import sys
from PIL import Image, ImageDraw

SIZE = 256
BG = "#1b2a41"
FRAME = "#0b1220"
SCREEN = "#f2c14e"
FG = "#ffffff"


def build(out_path: str) -> None:
    img = Image.new("RGB", (SIZE, SIZE), BG)
    d = ImageDraw.Draw(img)
    # frame + screen
    d.rounded_rectangle([28, 40, 228, 168], radius=10, fill=FRAME)
    d.rectangle([40, 52, 216, 156], fill=SCREEN)
    # play triangle
    d.polygon([(112, 78), (112, 130), (156, 104)], fill=FRAME)
    # stand
    d.rectangle([120, 168, 136, 196], fill=FRAME)
    d.rounded_rectangle([80, 196, 176, 210], radius=6, fill=FRAME)
    # little viewers (dots) in front
    for x in (70, 128, 186):
        d.ellipse([x - 10, 222, x + 10, 242], fill=FG)
    img.save(out_path, "PNG")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    build(sys.argv[1])
    print("wrote", sys.argv[1])
