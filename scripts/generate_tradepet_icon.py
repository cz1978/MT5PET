"""Create the small, dependency-light TradePet application icon.

The icon is intentionally drawn at 4x the largest target size and then
downsampled so the silhouette remains crisp in the 16 px title-bar variant.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "src" / "TradePet.App" / "Assets"
PNG_PATH = ASSET_DIR / "tradepet-icon.png"
ICO_PATH = ASSET_DIR / "tradepet-icon.ico"


def polygon(draw: ImageDraw.ImageDraw, points, fill):
    draw.polygon(points, fill=fill)


def draw_icon(size: int = 1024) -> Image.Image:
    scale = size / 256
    s = lambda value: int(round(value * scale))

    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # A restrained shadow keeps the mark separate from light desktop themes.
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    shadow_draw = ImageDraw.Draw(shadow)
    shadow_draw.rounded_rectangle((s(43), s(48), s(216), s(221)), radius=s(40), fill=(0, 0, 0, 120))
    shadow = shadow.filter(ImageFilter.GaussianBlur(s(7)))
    image.alpha_composite(shadow)

    # Rounded-square badge: dark navy with a thin jade and gold keyline.
    draw.rounded_rectangle((s(35), s(30), s(221), s(216)), radius=s(44), fill=(13, 21, 35, 255))
    draw.rounded_rectangle((s(40), s(35), s(216), s(211)), radius=s(40), outline=(34, 54, 73, 255), width=s(2))
    draw.rounded_rectangle((s(47), s(42), s(209), s(204)), radius=s(34), outline=(62, 177, 156, 130), width=s(2))

    # Tianlu guardian silhouette: horn, ears, head and compact body.
    gold = (211, 162, 73, 255)
    gold_light = (244, 202, 105, 255)
    gold_dark = (135, 94, 38, 255)
    jade = (71, 211, 177, 255)
    jade_dark = (22, 122, 109, 255)
    ink = (20, 29, 42, 255)
    ivory = (238, 241, 224, 255)

    # Horns and ears.
    polygon(draw, [(s(89), s(92)), (s(78), s(58)), (s(101), s(71)), (s(111), s(91))], gold)
    polygon(draw, [(s(166), s(91)), (s(178), s(57)), (s(155), s(71)), (s(145), s(91))], gold)
    polygon(draw, [(s(77), s(100)), (s(55), s(83)), (s(61), s(119)), (s(84), s(128))], gold_dark)
    polygon(draw, [(s(179), s(100)), (s(201), s(83)), (s(195), s(119)), (s(172), s(128))], gold_dark)

    # Body and mane.
    draw.ellipse((s(74), s(128), s(182), s(213)), fill=(35, 46, 59, 255), outline=(110, 126, 124, 180), width=s(2))
    polygon(draw, [(s(82), s(161)), (s(67), s(177)), (s(79), s(190)), (s(91), s(184)), (s(96), s(206)),
                   (s(112), s(192)), (s(128), s(211)), (s(144), s(192)), (s(160), s(206)), (s(165), s(184)),
                   (s(178), s(190)), (s(189), s(177)), (s(174), s(161))], gold_dark)

    # Face with a warm, enamel-like highlight.
    draw.rounded_rectangle((s(76), s(80), s(180), s(165)), radius=s(37), fill=(47, 59, 71, 255), outline=gold, width=s(3))
    draw.arc((s(86), s(87), s(170), s(151)), 205, 335, fill=(112, 131, 135, 210), width=s(2))
    draw.ellipse((s(94), s(111), s(112), s(131)), fill=jade_dark)
    draw.ellipse((s(144), s(111), s(162), s(131)), fill=jade_dark)
    draw.ellipse((s(99), s(114), s(106), s(121)), fill=ivory)
    draw.ellipse((s(149), s(114), s(156), s(121)), fill=ivory)
    draw.ellipse((s(120), s(130), s(136), s(143)), fill=ink)
    draw.arc((s(112), s(132), s(144), s(153)), 15, 165, fill=gold_light, width=s(3))

    # Jade collar and a small gold coin: the trading companion cue.
    draw.arc((s(86), s(145), s(170), s(192)), 15, 165, fill=jade, width=s(7))
    draw.ellipse((s(116), s(176), s(140), s(200)), fill=gold, outline=gold_light, width=s(2))
    draw.ellipse((s(122), s(182), s(134), s(194)), outline=jade_dark, width=s(2))
    draw.line((s(128), s(184), s(128), s(192)), fill=jade_dark, width=s(2))

    # A single glint makes the badge read cleanly at 16 px without adding text.
    draw.ellipse((s(61), s(55), s(66), s(60)), fill=(255, 255, 255, 180))
    return image


def main() -> None:
    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    master = draw_icon(1024)
    master.save(PNG_PATH, format="PNG", optimize=True)

    # ICO stores a native image for every common Windows surface.
    sizes = (256, 128, 64, 48, 32, 16)
    frames = [master.resize((size, size), Image.Resampling.LANCZOS) for size in sizes]
    frames[0].save(ICO_PATH, format="ICO", sizes=[(size, size) for size in sizes], append_images=frames[1:])


if __name__ == "__main__":
    main()
