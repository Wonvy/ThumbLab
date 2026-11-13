"""Build a synthetic preview archive and an archive with only a drawing image."""
from pathlib import Path
import struct
import sys
from zipfile import ZIP_DEFLATED, ZipFile
import zlib


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def sample_image():
    width, height = 128, 80
    rows = []
    for y in range(height):
        row = bytearray([0])
        for x in range(width):
            is_model = abs(x - 64) + 2 * abs(y - 39) < 54
            row.extend((69, 166, 204) if is_model else (18, 34, 51))
        rows.append(row)
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", header)
            + png_chunk(b"IDAT", zlib.compress(b"".join(rows))) + png_chunk(b"IEND", b""))


if __name__ == "__main__":
    output = Path(sys.argv[1])
    image = sample_image()
    for path, name in ((output, "model-preview.png"),
                       (output.with_name("drawing-only.shapr"), "drawings/test.png")):
        with ZipFile(path, "w", compression=ZIP_DEFLATED) as archive:
            archive.writestr("workspace", b"synthetic workspace")
            archive.writestr(name, image)
