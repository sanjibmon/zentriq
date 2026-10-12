"""Packs the rendered PNGs into the installer assets (ICO, BMP) and the web favicon. Developer tool only.
Usage: python3 build-assets.py <renderOutDir>"""
import io, shutil, struct, sys
from pathlib import Path
from PIL import Image

src = Path(sys.argv[1])
assets = Path(__file__).resolve().parent.parent
public = assets.parent.parent / 'frontend' / 'public'

def dib(img):
    """32-bit BGRA DIB frame (BITMAPINFOHEADER + bottom-up pixels + AND mask), the classic ICO entry format.
    Windows Installer and older shell code paths only render these; PNG frames are kept for 256 px only."""
    w, h = img.size
    px = img.tobytes('raw', 'BGRA')
    rows = [px[y * w * 4:(y + 1) * w * 4] for y in range(h)][::-1]
    mask_stride = ((w + 31) // 32) * 4
    mask = bytearray()
    a = img.getchannel('A').tobytes()
    for y in range(h - 1, -1, -1):
        row = bytearray(mask_stride)
        for x in range(w):
            if a[y * w + x] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(px) + len(mask), 0, 0, 0, 0)
    return header + b''.join(rows) + bytes(mask)

def ico(prefix, sizes, dest):
    frames = []
    for s in sizes:
        img = Image.open(src / f'{prefix}-{s}.png').convert('RGBA')
        if s >= 256:
            buf = io.BytesIO(); img.save(buf, format='PNG'); data = buf.getvalue()
        else:
            data = dib(img)
        frames.append((s, data))
    out = bytearray(struct.pack('<HHH', 0, 1, len(frames)))
    offset = 6 + 16 * len(frames)
    for s, data in frames:
        out += struct.pack('<BBBBHHII', s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in frames:
        out += data
    Path(dest).write_bytes(bytes(out))

ico('mark', [16, 20, 24, 32, 40, 48, 64, 96, 128, 256], assets / 'zentriq.ico')
ico('agent', [16, 20, 24, 32, 40, 48, 64, 96, 128, 256], assets / 'zentriq-agent.ico')
ico('mark', [16, 24, 32, 48, 64], public / 'favicon.ico')
Image.open(src / 'mark-64.png').save(assets / 'logo64.png')
Image.open(src / 'mark-512.png').save(assets / 'zentriq-512.png')
for name in ['WixUIDialog', 'WixUIBanner', 'splash', 'AgentDialog', 'AgentBanner']:
    Image.open(src / f'{name}.png').convert('RGB').save(assets / f'{name}.bmp')
shutil.copy(Path(__file__).parent / 'zentriq-mark.svg', public / 'zentriq-mark.svg')
print('assets written')
