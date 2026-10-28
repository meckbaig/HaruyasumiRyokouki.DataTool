"""Opening photos the same way for every step."""

import io


def open_upright_rgb(path):
    """Open an image (HEIC included) as RGB, rotated as the camera's EXIF orientation says."""
    from PIL import Image, ImageOps
    from pillow_heif import register_heif_opener

    register_heif_opener()
    with Image.open(path) as im:
        return ImageOps.exif_transpose(im).convert("RGB")


def jpeg_bytes(path, max_side):
    """Return JPEG bytes of the upright image, downscaled so its long side is at most max_side."""
    im = open_upright_rgb(path)
    if max_side:
        im.thumbnail((max_side, max_side))
    buf = io.BytesIO()
    im.save(buf, format="JPEG", quality=90)
    return buf.getvalue()
