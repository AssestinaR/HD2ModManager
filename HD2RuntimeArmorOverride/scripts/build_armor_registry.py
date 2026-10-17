"""Generate the self-contained runtime armor registry addon."""
import argparse
from pathlib import Path


def build(project: Path, output: Path) -> None:
    protocol = (project / "src/armor_registry_protocol.lua").read_text(encoding="utf-8")
    probe_core = (project / "src/armor_metadata_probe_core.lua").read_text(encoding="utf-8")
    runtime = (project / "src/armor_registry_runtime.lua").read_text(encoding="utf-8")
    transaction = (project / "src/armor_registry_transaction.lua").read_text(encoding="utf-8")
    composition = (project / "src/armor_registry_composition.lua").read_text(encoding="utf-8")
    probe = (project / "src/armor_metadata_probe.lua").read_text(encoding="utf-8")
    refresh_probe = (project / "src/armor_refresh_probe.lua").read_text(encoding="utf-8")
    world_probe = (project / "src/armor_refresh_world_probe.lua").read_text(encoding="utf-8")
    code_probe = (project / "src/armor_refresh_code_probe.lua").read_text(encoding="utf-8")
    route_probe = (project / "src/armor_refresh_route_probe.lua").read_text(encoding="utf-8")
    hotkey_probe = (project / "src/armor_refresh_hotkey.lua").read_text(encoding="utf-8")
    resource_probe = (project / "src/armor_refresh_resources.lua").read_text(encoding="utf-8")
    component_probe = (project / "src/armor_refresh_component.lua").read_text(encoding="utf-8")
    multisite_probe = (project / "src/armor_refresh_multisite.lua").read_text(encoding="utf-8")
    # 插件内 FFI 补丁模块：把护甲 bank 从 3 扩到 8。必须在 __hd2_runtime 之前
    # 定义，这样运行时的闭包能把它当 upvalue 捕获。
    bank_patch = (project / "src/bank_patch.lua").read_text(encoding="utf-8")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        f"local ARMOR_REGISTRY_PROTOCOL=(function()\n{protocol}\nend)()\n"
        f"local ARMOR_METADATA_PROBE_CORE=(function()\n{probe_core}\nend)()\n"
        f"local ARMOR_REGISTRY_TRANSACTION=(function()\n{transaction}\nend)()\n"
        f"local ARMOR_REGISTRY_COMPOSITION=(function()\n{composition}\nend)()\n"
        f"local ARMOR_REFRESH_PROBE=(function()\n{refresh_probe}\nend)()\n"
        f"local ARMOR_REFRESH_WORLD_PROBE=(function()\n{world_probe}\nend)()\n"
        f"local ARMOR_REFRESH_CODE_PROBE=(function()\n{code_probe}\nend)()\n"
        f"local ARMOR_REFRESH_ROUTE_PROBE=(function()\n{route_probe}\nend)()\n"
        f"local ARMOR_REFRESH_HOTKEY=(function()\n{hotkey_probe}\nend)()\n"
        f"local ARMOR_REFRESH_RESOURCES=(function()\n{resource_probe}\nend)()\n"
        f"local ARMOR_REFRESH_COMPONENT=(function()\n{component_probe}\nend)()\n"
        f"local ARMOR_REFRESH_MULTISITE=(function()\n{multisite_probe}\nend)()\n"
        f"local ARMOR_BANK_PATCH=(function()\n{bank_patch}\nend)()\n"
        f"local __hd2_runtime=(function()\n{runtime}\nend)()\n"
        # 补丁在运行时初始化之后、探针之前安装。用 pcall 包住：失败只记录，
        # 绝不让整个插件加载失败（否则玩家会连原版护甲都没有）。
        "local __hd2_bank_patch_state=nil\n"
        "do\n"
        "  local ok,state_or_err=pcall(ARMOR_BANK_PATCH.install)\n"
        "  if ok and type(state_or_err)=='table' then\n"
        "    __hd2_bank_patch_state=state_or_err\n"
        "  else\n"
        "    __hd2_bank_patch_error=tostring(state_or_err)\n"
        "  end\n"
        "end\n"
        f"{probe}",
        encoding="utf-8",
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    build(Path(__file__).resolve().parent.parent, args.output)
