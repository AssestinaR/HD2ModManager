"""Crop a grid figure into per-cell PNGs and upscale for close inspection."""
import sys, os
from PIL import Image

def main():
    src = sys.argv[1]
    cols = int(sys.argv[2]) if len(sys.argv) > 2 else 4
    rows = int(sys.argv[3]) if len(sys.argv) > 3 else 2
    scale = float(sys.argv[4]) if len(sys.argv) > 4 else 2.0
    im = Image.open(src).convert('RGB')
    W, H = im.size
    print('source %s  %dx%d  grid %dx%d' % (src, W, H, cols, rows))
    base = os.path.splitext(src)[0]
    for r in range(rows):
        for c in range(cols):
            x0 = int(W * c / cols); x1 = int(W * (c + 1) / cols)
            y0 = int(H * r / rows); y1 = int(H * (r + 1) / rows)
            cell = im.crop((x0, y0, x1, y1))
            nw, nh = int(cell.width * scale), int(cell.height * scale)
            cell = cell.resize((nw, nh), Image.LANCZOS)
            out = '%s_r%dc%d.png' % (base, r + 1, c + 1)
            cell.save(out)
            print('  ->', out, cell.size)

main()
