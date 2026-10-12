"""Packs the rendered PNGs into the installer assets (ICO, BMP) and the web favicon. Developer tool only.
Usage: python3 build-assets.py <renderOutDir>"""
import shutil, sys
from pathlib import Path
from PIL import Image

src = Path(sys.argv[1])
assets = Path(__file__).resolve().parent.parent
public = assets.parent.parent / 'frontend' / 'public'

def ico(prefix, sizes, dest):
    imgs = [Image.open(src / f'{prefix}-{s}.png').convert('RGBA') for s in sizes]
    big = imgs[-1]
    big.save(dest, format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[:-1])

ico('mark', [16, 20, 24, 32, 40, 48, 64, 96, 128, 256], assets / 'zentriq.ico')
ico('agent', [16, 20, 24, 32, 40, 48, 64, 96, 128, 256], assets / 'zentriq-agent.ico')
ico('mark', [16, 24, 32, 48, 64], public / 'favicon.ico')
Image.open(src / 'mark-64.png').save(assets / 'logo64.png')
Image.open(src / 'mark-512.png').save(assets / 'zentriq-512.png')
for name in ['WixUIDialog', 'WixUIBanner', 'splash', 'AgentDialog', 'AgentBanner']:
    Image.open(src / f'{name}.png').convert('RGB').save(assets / f'{name}.bmp')
shutil.copy(Path(__file__).parent / 'zentriq-mark.svg', public / 'zentriq-mark.svg')
print('assets written')
