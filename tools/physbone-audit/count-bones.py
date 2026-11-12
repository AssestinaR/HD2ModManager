"""统计骨名在一个 `.gpu_resources` 里出现几次，用来判断名字表是"共享"还是"被复制了 N 份"。

用途（2026-11-12）：排查"一个玩家被挂了 7 件护甲"时，要区分两件事：
  · 名字表共享（每个骨名 1 次）  ⇒ 数据只存一份，问题是"7 个 unit 的骨架都引用了它"；
  · 名字表复制 7 份（每名 7 次）  ⇒ 数据真的被复制了，先修转换器的复用。
两种情况修法完全不同，所以先数一遍。

用法：
    python tools/physbone-audit/count-bones.py "E:\\mod\\Lunalice_Bill\\sectional\\9ba626afa44a3aa3.patch_0.gpu_resources"
    python tools/physbone-audit/count-bones.py <文件> --name j4ad8bc381c40/source_extra_64
"""

from __future__ import annotations

import argparse
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("file", help="要统计的 .gpu_resources")
    parser.add_argument("--name", action="append", default=None,
                        help="额外指定的骨名（可重复）；不给就探默认的几个")
    args = parser.parse_args()

    path = Path(args.file)
    if not path.is_file():
        print(f"文件不存在：{path}")
        return 2
    data = path.read_bytes()
    print(f"文件: {path.name}（{len(data)} 字节）\n")

    probes = args.name or [
        "j4ad8bc381c40/source_extra_64",
        "j4ad8bc381c40/source_extra_65",
        "j4ad8bc381c40/source_extra_126",
        "source_extra_",
        "HD2SRG01",
        "shinano_body",
    ]
    for probe in probes:
        needle = probe.encode() if isinstance(probe, str) else probe
        print(f"{probe:44s} × {data.count(needle)}")

    print("\n判读：")
    print("  · 具体骨名各 1 次 ⇒ 名字表共享（本仓实测的 Lunalice 就是这种）。")
    print("  · 具体骨名 N 次   ⇒ 名字表被复制了 N 份，转换器在重复写入。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
