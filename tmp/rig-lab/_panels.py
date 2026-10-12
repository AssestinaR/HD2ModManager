import numpy as np
from PIL import Image
im=np.asarray(Image.open("out/armpit.png").convert("RGB")).astype(np.int16)
h,w,_=im.shape
print("shape",im.shape)
# 背景 = 深色 (~ (18,20,26))? 取每列的非背景像素比例
bg=np.array([18,20,26])
d=np.abs(im-bg).sum(2)
fg=(d>40)
colfg=fg.mean(0)          # 每列前景比例
rowfg=fg.mean(1)
# 打印前景比例突变的位置
def runs(v,thr):
    out=[];cur=None
    for i,x in enumerate(v):
        if x>thr and cur is None: cur=i
        elif x<=thr and cur is not None: out.append((cur,i-1)); cur=None
    if cur is not None: out.append((cur,len(v)-1))
    return out
print("col runs (thr .02):", [r for r in runs(colfg,0.02) if r[1]-r[0]>20])
print("row runs (thr .02):", [r for r in runs(rowfg,0.02) if r[1]-r[0]>20])
