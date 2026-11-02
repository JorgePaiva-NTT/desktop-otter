"""Append a style-matched curled sleeping loop to the validated otter atlas.

The source rows are retained exactly, apart from row 2: every left-running
cell is regenerated as the pixel-perfect horizontal mirror of its clean
right-running counterpart in row 1. This prevents neighboring-frame overlap
artifacts. The eight sleeping frames in row 11 are derived from the illustrated
transparent sleep pose and Zzz overlay in ``Tools/sleep-*-reference.png``.
Keeping both as artwork, rather than drawing a body from ellipses, attaching a
cropped head, or using plain text, preserves the line work, fur shading,
anatomy, and proportions of the rest of the pet.
"""

from argparse import ArgumentParser
from hashlib import sha256
from math import cos, pi
from pathlib import Path

from PIL import Image, ImageDraw


CELL_WIDTH = 192
CELL_HEIGHT = 208
SLEEP_ROW = 11
FRAME_COUNT = 8
SCALE = 4
BASELINE = 203
POSE_WIDTH = 166
POSE_HEIGHT = 172
POSE_REFERENCE = Path(__file__).with_name("sleep-pose-reference.png")
Z_REFERENCE = Path(__file__).with_name("sleep-z-reference.png")
Z_WIDTH = 56
Z_HEIGHT = 58


def load_pose() -> Image.Image:
    """Load, trim, and register the clean full-body sleeping illustration."""
    source = Image.open(POSE_REFERENCE).convert("RGBA")
    bounds = source.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("Sleeping pose reference has no visible pixels")

    source = source.crop(bounds)
    return source.resize((POSE_WIDTH * SCALE, POSE_HEIGHT * SCALE), Image.Resampling.LANCZOS)


def load_sleep_symbols() -> Image.Image:
    """Load the illustrated Zzz cluster with transparent margins removed."""
    source = Image.open(Z_REFERENCE).convert("RGBA")
    bounds = source.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("Sleep-symbol reference has no visible pixels")
    return source.crop(bounds).resize((Z_WIDTH * SCALE, Z_HEIGHT * SCALE), Image.Resampling.LANCZOS)


def with_opacity(image: Image.Image, opacity: int) -> Image.Image:
    """Fade the complete overlay without flattening its painted edges."""
    if opacity >= 255:
        return image
    faded = image.copy()
    faded.putalpha(faded.getchannel("A").point(lambda alpha: alpha * opacity // 255))
    return faded


def build_frames() -> list[Image.Image]:
    """Create an eight-frame, baseline-locked breathing loop from the pose."""
    pose = load_pose()
    symbols = load_sleep_symbols()
    frames: list[Image.Image] = []

    for column in range(FRAME_COUNT):
        # The entire silhouette expands very slightly when breathing. It is
        # intentionally subtle: sleeping should feel calm and never bob, jump,
        # or change apparent character scale.
        breath = (1 - cos(column * 2 * pi / FRAME_COUNT)) / 2
        width = round(pose.width * (1 + breath * 0.008))
        height = round(pose.height * (1 + breath * 0.014))
        breathing = pose.resize((width, height), Image.Resampling.LANCZOS)

        cell = Image.new("RGBA", (CELL_WIDTH * SCALE, CELL_HEIGHT * SCALE))
        horizontal = (cell.width - width) // 2
        vertical = BASELINE * SCALE - height
        cell.alpha_composite(breathing, (horizontal, vertical))

        # The illustrated Zzz cluster rises slowly as the otter breathes. It
        # fades near the loop edge, so the return to its starting point reads
        # as a fresh sleepy exhale rather than a visible jump.
        progress = column / FRAME_COUNT
        z_width = round(symbols.width * (0.90 + progress * 0.10))
        z_height = round(symbols.height * (0.90 + progress * 0.10))
        floating_symbols = symbols.resize((z_width, z_height), Image.Resampling.LANCZOS)
        floating_symbols = with_opacity(floating_symbols, [100, 140, 190, 238, 255, 230, 190, 140][column])
        z_left = round((112 + progress * 8) * SCALE)
        z_top = round((17 - progress * 14) * SCALE)
        cell.alpha_composite(floating_symbols, (z_left, z_top))
        cell = cell.resize((CELL_WIDTH, CELL_HEIGHT), Image.Resampling.LANCZOS)

        bounds = cell.getchannel("A").getbbox()
        if bounds is None or bounds[0] < 2 or bounds[2] > CELL_WIDTH - 2 or bounds[3] > CELL_HEIGHT - 1:
            raise ValueError(f"Sleeping frame {column} exceeds its safety border: {bounds}")
        frames.append(cell)

    if len({sha256(frame.tobytes()).digest() for frame in frames}) < 4:
        raise ValueError("Sleeping loop does not contain enough distinct frames")
    return frames


def generate(source: Path, output: Path, preview: Path) -> None:
    atlas = Image.open(source).convert("RGBA")
    original_height = SLEEP_ROW * CELL_HEIGHT
    valid_sizes = {
        (CELL_WIDTH * FRAME_COUNT, original_height),
        (CELL_WIDTH * FRAME_COUNT, original_height + CELL_HEIGHT),
    }
    if atlas.size not in valid_sizes:
        raise ValueError(f"Unexpected source atlas dimensions: {atlas.size}")

    original = atlas.crop((0, 0, atlas.width, original_height))
    result = Image.new("RGBA", (atlas.width, original_height + CELL_HEIGHT))
    result.paste(original)
    frames = build_frames()
    for column, frame in enumerate(frames):
        result.paste(frame, (column * CELL_WIDTH, original_height))

    # The supplied left-running row contains sprite bleed in several frames.
    # Rebuild it cell-by-cell from the normalized right-running row instead of
    # mirroring a whole strip, which could copy adjacent-cell pixels.
    for column in range(FRAME_COUNT):
        left = column * CELL_WIDTH
        right_running = original.crop((left, CELL_HEIGHT, left + CELL_WIDTH, CELL_HEIGHT * 2))
        left_running = right_running.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
        result.paste(left_running, (left, CELL_HEIGHT * 2))

    # Never allow the generator to alter a pre-existing animation except the
    # explicitly repaired running-left row.
    assert result.crop((0, 0, atlas.width, CELL_HEIGHT * 2)).tobytes() == original.crop((0, 0, atlas.width, CELL_HEIGHT * 2)).tobytes()
    assert result.crop((0, CELL_HEIGHT * 3, atlas.width, original_height)).tobytes() == original.crop((0, CELL_HEIGHT * 3, atlas.width, original_height)).tobytes()
    for column in range(FRAME_COUNT):
        left = column * CELL_WIDTH
        right_running = result.crop((left, CELL_HEIGHT, left + CELL_WIDTH, CELL_HEIGHT * 2))
        repaired_left = result.crop((left, CELL_HEIGHT * 2, left + CELL_WIDTH, CELL_HEIGHT * 3))
        assert repaired_left.tobytes() == right_running.transpose(Image.Transpose.FLIP_LEFT_RIGHT).tobytes()
    output.parent.mkdir(parents=True, exist_ok=True)
    result.save(output, optimize=True)

    contact = Image.new("RGBA", (CELL_WIDTH * 4, CELL_HEIGHT * 2), (237, 241, 239, 255))
    drawing = ImageDraw.Draw(contact)
    for index, frame in enumerate(frames):
        horizontal = index % 4 * CELL_WIDTH
        vertical = index // 4 * CELL_HEIGHT
        drawing.ellipse((horizontal + 18, vertical + 187, horizontal + 174, vertical + 205), fill=(83, 151, 163, 255))
        contact.alpha_composite(frame, (horizontal, vertical))
    preview.parent.mkdir(parents=True, exist_ok=True)
    contact.convert("RGB").save(preview)

    print(f"PASS: preserved source rows, repaired 8 running-left cells, and added {len(frames)} illustrated breathing frames at row {SLEEP_ROW}; atlas {result.size}")
    print(f"Atlas: {output}")
    print(f"Preview: {preview}")


def main() -> None:
    parser = ArgumentParser()
    parser.add_argument(
        "source",
        type=Path,
        nargs="?",
        default=Path("../otter-pet/final/spritesheet-extended.png"),
        help="validated 11-row (or previously extended) 1536x2288 atlas",
    )
    parser.add_argument("--output", type=Path, default=Path("Assets/otter-spritesheet-v2.png"))
    parser.add_argument("--preview", type=Path, default=Path("obj/sleeping-preview.png"))
    args = parser.parse_args()
    generate(args.source, args.output, args.preview)


if __name__ == "__main__":
    main()
