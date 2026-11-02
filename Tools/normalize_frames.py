"""Normalize apparent sprite scale and registration across the complete pet atlas."""

from argparse import ArgumentParser
from pathlib import Path

from PIL import Image


CELL_WIDTH = 192
CELL_HEIGHT = 208
TARGET_BASELINE = 203
FRAME_COUNTS = [6, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8]

# One transform per animation family preserves real pose changes inside a row.
# Upright families target the 186px idle height; sideways running targets a
# comparable 188px long axis; jumping retains its crouch/lift geometry.
ROW_SCALES = [1.00, 1.03, 1.03, 0.94, 1.02, 0.94, 0.94, 0.94, 0.94, 1.02, 1.02]


def normalize(source: Path, output: Path) -> None:
    atlas = Image.open(source).convert("RGBA")

    for row, frame_count in enumerate(FRAME_COUNTS):
        # Running-left is derived after the clean rightward row is normalized.
        # Mirroring one complete cell at a time prevents neighboring-pose bleed
        # and preserves temporal order.
        if row == 2:
            continue

        cells: list[tuple[Image.Image, tuple[int, int, int, int]]] = []
        for column in range(frame_count):
            left = column * CELL_WIDTH
            top = row * CELL_HEIGHT
            cell = atlas.crop((left, top, left + CELL_WIDTH, top + CELL_HEIGHT))
            bounds = cell.getchannel("A").getbbox()
            if bounds is None:
                raise SystemExit(f"row {row}, frame {column} is empty")
            cells.append((cell, bounds))

        source_ground = max(bounds[3] for _, bounds in cells)
        for column, (cell, bounds) in enumerate(cells):
            sprite = cell.crop(bounds)
            scale = 186 / sprite.height if row == 0 else ROW_SCALES[row]
            width = max(1, round(sprite.width * scale))
            height = max(1, round(sprite.height * scale))

            # Keep a small transparent safety border even for wide running poses.
            fit = min(1.0, 188 / width, 200 / height)
            width = max(1, round(width * fit))
            height = max(1, round(height * fit))
            effective_scale = scale * fit
            sprite = sprite.resize((width, height), Image.Resampling.LANCZOS)

            if row == 0:
                bottom = TARGET_BASELINE
            else:
                lift = round((source_ground - bounds[3]) * effective_scale)
                bottom = TARGET_BASELINE - lift

            x = (CELL_WIDTH - width) // 2
            y = bottom - height
            if x < 0 or y < 0 or x + width > CELL_WIDTH or y + height > CELL_HEIGHT:
                raise SystemExit(f"normalized row {row}, frame {column} does not fit its cell")

            normalized = Image.new("RGBA", (CELL_WIDTH, CELL_HEIGHT), (0, 0, 0, 0))
            normalized.alpha_composite(sprite, (x, y))
            atlas.paste(normalized, (column * CELL_WIDTH, row * CELL_HEIGHT))

    for column in range(FRAME_COUNTS[2]):
        left = column * CELL_WIDTH
        rightward = atlas.crop((left, CELL_HEIGHT, left + CELL_WIDTH, CELL_HEIGHT * 2))
        leftward = rightward.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
        atlas.paste(leftward, (left, CELL_HEIGHT * 2))

    output.parent.mkdir(parents=True, exist_ok=True)
    atlas.save(output, optimize=True)


def main() -> None:
    parser = ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("output")
    args = parser.parse_args()
    normalize(Path(args.source), Path(args.output))


if __name__ == "__main__":
    main()
