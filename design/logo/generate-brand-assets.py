from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[2]
MASTER = Path(__file__).with_name("RadioVault-logo-refined.png")
SERVER_MASTER = Path(__file__).with_name("RadioVault-server-logo-v3-source.png")
DESKTOP_ASSETS = ROOT / "TheRadioVault.Desktop.Avalonia" / "Assets"
SERVER_ASSETS = ROOT / "TheRadioVault.Server" / "Assets"
WEB_ASSETS = ROOT / "TheRadioVault.Web" / "Assets"

ACCENT = (249, 198, 50, 255)
DARK = (27, 32, 40, 255)


def alpha_crop(image: Image.Image) -> Image.Image:
    alpha = image.getchannel("A")
    bounds = alpha.getbbox()
    if bounds is None:
        raise RuntimeError("The selected logo is fully transparent.")
    return image.crop(bounds)


def contain(image: Image.Image, size: int, padding: float, background=(0, 0, 0, 0)) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), background)
    available = round(size * (1 - (padding * 2)))
    fitted = image.copy()
    fitted.thumbnail((available, available), Image.Resampling.LANCZOS)
    x = (size - fitted.width) // 2
    y = (size - fitted.height) // 2
    canvas.alpha_composite(fitted, (x, y))
    return canvas


def save_png(image: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, "PNG", optimize=True)


def extract_generated_tile(image: Image.Image) -> Image.Image:
    rgb = image.convert("RGB")
    tile_mask = Image.new("L", rgb.size)
    tile_mask.putdata([
        255 if red > 145 and green > 85 and blue < 150 and red > green else 0
        for red, green, blue in rgb.get_flattened_data()
    ])
    bounds = tile_mask.getbbox()
    if bounds is None:
        raise RuntimeError("The generated server logo contains no detectable yellow tile.")
    tile = rgb.crop(bounds).convert("RGBA")
    alpha = Image.new("L", tile.size)
    draw = ImageDraw.Draw(alpha)
    radius = round(min(tile.size) * 0.145)
    draw.rounded_rectangle((1, 1, tile.width - 2, tile.height - 2), radius=radius, fill=255)
    alpha = alpha.filter(ImageFilter.GaussianBlur(0.7))
    tile.putalpha(alpha)
    return tile


def save_server_preview(server_logo: Image.Image) -> None:
    canvas = Image.new("RGBA", (360, 96), DARK)
    draw = ImageDraw.Draw(canvas)
    sizes = (16, 24, 32, 48)
    x = 34
    for size in sizes:
        icon = server_logo.resize((size, size), Image.Resampling.LANCZOS)
        canvas.alpha_composite(icon, (x + ((48 - size) // 2), 12 + ((48 - size) // 2)))
        draw.text((x + 24, 69), f"{size}px", anchor="mm", fill=(210, 214, 220, 255))
        x += 82
    save_png(canvas, Path(__file__).with_name("RadioVault-server-logo-small-preview.png"))


def main() -> None:
    source = Image.open(MASTER).convert("RGBA")
    mark = alpha_crop(source)

    display_logo = contain(mark, 512, 0.035)
    save_png(display_logo, DESKTOP_ASSETS / "RadioVault-Logo.png")
    save_png(display_logo, WEB_ASSETS / "app-logo-512.png")

    standard_512 = contain(mark, 512, 0.035)
    standard_192 = contain(mark, 192, 0.035)
    # Apple applies its own rounded Home Screen mask. Give it an opaque,
    # full-bleed brand canvas so the transparent corners of the master cannot
    # be flattened to a black square by Safari.
    apple_180 = contain(mark, 180, 0.035, ACCENT).convert("RGB")
    maskable_512 = contain(mark, 512, 0.105, ACCENT).convert("RGB")

    save_png(standard_512, WEB_ASSETS / "app-icon-512.png")
    save_png(standard_192, WEB_ASSETS / "app-icon-192.png")
    save_png(apple_180, WEB_ASSETS / "app-icon-180.png")
    save_png(maskable_512, WEB_ASSETS / "app-icon-maskable-512.png")

    ico_base = contain(mark, 256, 0.035)
    ico_base.save(
        DESKTOP_ASSETS / "RadioVault.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    if SERVER_MASTER.exists():
        server_source = extract_generated_tile(Image.open(SERVER_MASTER))
        server_logo = contain(alpha_crop(server_source), 512, 0.035)
        save_png(server_logo, Path(__file__).with_name("RadioVault-server-logo-v3.png"))
        save_png(server_logo, SERVER_ASSETS / "RadioVault.Server-Logo.png")
        server_ico = contain(alpha_crop(server_source), 256, 0.035)
        SERVER_ASSETS.mkdir(parents=True, exist_ok=True)
        server_ico.save(
            SERVER_ASSETS / "RadioVault.Server.ico",
            format="ICO",
            sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
        )
        save_server_preview(server_logo)

    print("Generated Radio Vault client, server, installer and Web icon assets.")


if __name__ == "__main__":
    main()
