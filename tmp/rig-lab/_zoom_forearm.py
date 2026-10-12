# -*- coding: utf-8 -*-
from PIL import Image, ImageDraw
im = Image.open("out/forearm-curve.png")
print("src", im.size)
labels = ["no arm_rot", "ALL (current)", "no elbow rot", "REFERENCE"]
idx = [0, 1, 3, 5]
PW, GAP, X0, Y0 = 470, 12, 12, 96
crops = []
for i in idx:
    cx = X0 + i * (PW + GAP)
    c = im.crop((cx + 60, Y0 + 160, cx + 300, Y0 + 430)).resize((240*3, 270*3), Image.LANCZOS)
    crops.append(c)
W = len(crops) * (720 + 12) + 12
H = 270*3 + 60
out = Image.new("RGB", (W, H), (26, 28, 34))
dr = ImageDraw.Draw(out)
dr.text((12, 8), "round 45e step 1: LEFT forearm zoomed (front view), red = slice-centroid polyline", fill=(232,232,244))
dr.text((12, 28), "cyan = shoulder->elbow->hand bone chain.  the red line should be STRAIGHT from the elbow down to the wrist.", fill=(150,160,180))
for k, c in enumerate(crops):
    x = 12 + k * (720 + 12)
    dr.text((x + 4, 50), labels[k], fill=(205,214,228))
    out.paste(c, (x, 66))
p = "out/forearm-zoom.png"
out.save(p)
print("wrote", p, out.size)
