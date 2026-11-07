"""Generates the placeholder window icon (default-<size>.png): orange rounded square with a white dot.
Replace the PNGs by the real artwork any time; keep the names, CMakeLists.txt embeds default-*.png."""
import struct
import zlib

SIZES = (16, 24, 32, 48, 64, 128, 256)
SS = 4  # supersampling per axis


def inside_round_rect(x, y, r):
    # unit square [0,1]^2 with corner radius r
    cx = min(max(x, r), 1 - r)
    cy = min(max(y, r), 1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def pixel(px, py, size):
    acc = [0.0, 0.0, 0.0, 0.0]
    for sy in range(SS):
        for sx in range(SS):
            x = (px + (sx + 0.5) / SS) / size
            y = (py + (sy + 0.5) / SS) / size
            if not inside_round_rect(x, y, 0.22):
                continue
            if (x - 0.5) ** 2 + (y - 0.5) ** 2 <= 0.26 ** 2:
                c = (255, 255, 255)
            else:
                c = (255, 153, 0)
            acc[0] += c[0]
            acc[1] += c[1]
            acc[2] += c[2]
            acc[3] += 255
    n = SS * SS
    a = acc[3] / n
    if a == 0:
        return (0, 0, 0, 0)
    k = n * a / 255  # un-premultiply
    return (round(acc[0] / k), round(acc[1] / k), round(acc[2] / k), round(a))


def png(size):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            raw.extend(pixel(x, y, size))

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


for s in SIZES:
    with open(f"default-{s}.png", "wb") as f:
        f.write(png(s))
