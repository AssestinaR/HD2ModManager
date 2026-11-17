# LAB ONLY (V5): inspect the delta 'neutral' animation where decoders disagree
import sys, json, runpy
sio = json.load(open(sys.argv[2]))
a = sio["anims"]["d.a_adjustment_cstrike_neutral"]
for bone, fr in a["frames"].items():
    rots = fr["rot"]; zero = [i for i, q in enumerate(rots) if sum(x*x for x in q) < 1e-9]
    print(bone, "frames", len(rots), "zero-quaternion frames:", (zero[:3], len(zero)), "f0", [round(x,4) for x in rots[0]], "f120", [round(x,4) for x in rots[-1]])
