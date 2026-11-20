"""PWA のアイコン（PNG）を標準ライブラリだけで作る開発用の道具。

使い方: python tools/make_icons.py
出力:   icons/icon-192.png, icons/icon-512.png, icons/apple-touch-icon.png
絵柄:   紺の地に、白い板と、その上に置いた橙の四角形（描画した四角形のイメージ）。
"""
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BG = (43, 58, 74)
SHEET = (245, 247, 250)
GRID = (205, 213, 222)
RECT = (232, 163, 61)
EDGE = (180, 110, 20)


def color_at(u: float, v: float) -> tuple:
    """u, v は 0〜1 の位置（左上が 0）。"""
    # 板: 余白 18%
    if 0.18 <= u <= 0.82 and 0.24 <= v <= 0.76:
        # 四角形
        if 0.30 <= u <= 0.62 and 0.36 <= v <= 0.64:
            if u < 0.315 or u > 0.605 or v < 0.375 or v > 0.625:
                return EDGE
            return RECT
        gu = (u - 0.18) / 0.64 * 8
        gv = (v - 0.24) / 0.52 * 6
        if abs(gu - round(gu)) < 0.04 or abs(gv - round(gv)) < 0.05:
            return GRID
        return SHEET
    return BG


def png(size: int) -> bytes:
    ss = 3  # 縁をなめらかにするため 3×3 で平均する
    rows = []
    for y in range(size):
        row = bytearray([0])
        for x in range(size):
            acc = [0, 0, 0]
            for j in range(ss):
                for i in range(ss):
                    c = color_at((x + (i + 0.5) / ss) / size, (y + (j + 0.5) / ss) / size)
                    for k in range(3):
                        acc[k] += c[k]
            row += bytes(round(a / (ss * ss)) for a in acc)
        rows.append(bytes(row))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack('>IIBBBBB', size, size, 8, 2, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b''))


def main() -> None:
    out = ROOT / 'icons'
    out.mkdir(exist_ok=True)
    for name, size in [('icon-192.png', 192), ('icon-512.png', 512), ('apple-touch-icon.png', 180)]:
        (out / name).write_bytes(png(size))
        print('wrote', out / name)


if __name__ == '__main__':
    main()
