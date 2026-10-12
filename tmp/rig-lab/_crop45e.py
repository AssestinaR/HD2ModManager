import os
from PIL import Image, ImageDraw
SRC = r"C:/Users/Administrator/.workbuddy-ai/clipboard-images/clipboard-2026-10-12T08-20-07-475Z-9a744f3c.jpg"
im = Image.open(SRC)
W, H = im.size
print("size", W, H)
out = "out"
os.makedirs(out, exist_ok=True)

def crop_grid(name, box, zoom=3, step=25):
    x0, y0, x1, y1 = box
    c = im.crop(box)
    c = c.resize((c.width*zoom, c.height*zoom), Image.LANCZOS)
    d = ImageDraw.Draw(c)
    # 每 step(原图px) 画一条网格线并标原图坐标
    gx = (x0 // step) * step
    while gx <= x1:
        X = (gx - x0) * zoom
        if X >= 0:
            d.line([(X, 0), (X, c.height)], fill=(255, 80, 80), width=1)
            d.text((X + 2, 2), str(gx), fill=(255, 160, 160))
        gx += step
    gy = (y0 // step) * step
    while gy <= y1:
        Y = (gy - y0) * zoom
        if Y >= 0:
            d.line([(0, Y), (c.width, Y)], fill=(80, 200, 255), width=1)
            d.text((2, Y + 2), str(gy), fill=(160, 220, 255))
        gy += step
    p = os.path.join(out, name + ".png")
    c.save(p)
    print("wrote", p, c.size)

# 左臂整体（观察者左侧 = 角色右手）
crop_grid("e-L-arm", (0, 60, 620, 560), zoom=2, step=50)
# 右臂整体
crop_grid("e-R-arm", (1120, 60, 1744, 560), zoom=2, step=50)
