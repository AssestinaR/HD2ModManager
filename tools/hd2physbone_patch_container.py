#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Pack and unpack HD2 PhysBone resources in a Patch GPU sidecar trailer.

This tool intentionally uses only the Python standard library.  It does not
understand the internal contents of the PhysBone binaries; it stores and
restores them byte-for-byte.

The container is appended to the end of ``<patch>.gpu_resources``.  The Patch
TOC is never changed and no TOC entry points at the container.  The game sees
only its normal TOC-referenced GPU ranges; the PhysBone add-on can discover
the container through the footer at EOF.

Commands:
    inspect PATCH
    pack PATCH RESOURCE_DIR --output-dir OUT_DIR
    unpack PATCH --output-dir OUT_DIR
    strip PATCH --output-dir OUT_DIR

``merge`` is an alias for ``pack`` and ``split`` is an alias for ``unpack``.
The default operation always writes a new copy.  Use ``--in-place`` only when
the caller explicitly wants to replace the source GPU sidecar.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import struct
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple


PATCH_MAGIC = 0xF0000011
CONTAINER_MAGIC = b"HD2PBX01"
FOOTER_MAGIC = b"HD2PBFT1"
CONTAINER_VERSION = 1

HEADER_FORMAT = "<8sIIQQQII"
FOOTER_FORMAT = "<8sQQ32s"
RECORD_FIXED_FORMAT = "<HHQQ32sI"
HEADER_SIZE = struct.calcsize(HEADER_FORMAT)
FOOTER_SIZE = struct.calcsize(FOOTER_FORMAT)
RECORD_FIXED_SIZE = struct.calcsize(RECORD_FIXED_FORMAT)

FLAG_PREFIX_LENGTH = 0x00000001

LEGACY_TYPE_OFFSET = 60
SLIM_TYPE_OFFSET = 72
TYPE_RECORD_SIZE = 32
ENTRY_RECORD_SIZE = 80
ALIGNMENT = 64

MAX_RECORDS = 256
MAX_NAME_BYTES = 1024
MAX_CONTAINER_BYTES = 1024 * 1024 * 1024
MAX_RECORD_BYTES = 512 * 1024 * 1024

ROLE_NAMES = (
    "lua_units.txt",
    "hd2_ib_needle.bin",
    "hd2_spring_rig.bin",
)


class ContainerError(Exception):
    """A malformed or unsafe embedded container."""


@dataclass(frozen=True)
class PatchGpuRange:
    offset: int
    size: int


@dataclass(frozen=True)
class ResourceRecord:
    name: str
    data: bytes

    @property
    def sha256(self) -> bytes:
        return hashlib.sha256(self.data).digest()


@dataclass(frozen=True)
class EmbeddedContainer:
    start: int
    length: int
    version: int
    flags: int
    index_offset: int
    index_length: int
    records: Tuple[ResourceRecord, ...]
    footer_field: int
    digest: bytes

    @property
    def has_prefix_length(self) -> bool:
        return bool(self.flags & FLAG_PREFIX_LENGTH)

    @property
    def prefix_length(self) -> Optional[int]:
        return self.footer_field if self.has_prefix_length else None


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def align_up(value: int, alignment: int = ALIGNMENT) -> int:
    return ((value + alignment - 1) // alignment) * alignment


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def read_u64(data: bytes, offset: int) -> int:
    return struct.unpack_from("<Q", data, offset)[0]


def resolve_toc_entries_offset(data: bytes) -> Tuple[int, int, int]:
    if len(data) < 12 or read_u32(data, 0) != PATCH_MAGIC:
        raise ContainerError("the input file is not a supported HD2 Patch TOC")

    type_count = read_u32(data, 4)
    file_count = read_u32(data, 8)
    legacy = LEGACY_TYPE_OFFSET + type_count * TYPE_RECORD_SIZE
    slim = SLIM_TYPE_OFFSET + type_count * TYPE_RECORD_SIZE

    def score(type_offset: int, entry_offset: int) -> int:
        required = entry_offset + file_count * ENTRY_RECORD_SIZE
        if required > len(data):
            return -2**31
        type_ids = set()
        declared = 0
        for index in range(type_count):
            offset = type_offset + index * TYPE_RECORD_SIZE
            type_ids.add(read_u64(data, offset + 8))
            declared += read_u64(data, offset + 16)
        result = 1000 if declared == file_count else 0
        for index in range(file_count):
            offset = entry_offset + index * ENTRY_RECORD_SIZE
            if read_u64(data, offset) != 0:
                result += 1
            if read_u64(data, offset + 8) in type_ids:
                result += 10
        return result

    legacy_score = score(LEGACY_TYPE_OFFSET, legacy)
    slim_score = score(SLIM_TYPE_OFFSET, slim)
    entry_offset = slim if type_count and slim_score > legacy_score else legacy
    return entry_offset, type_count, file_count


def read_gpu_ranges(patch_path: Path) -> Tuple[List[PatchGpuRange], int]:
    data = patch_path.read_bytes()
    entry_offset, _, file_count = resolve_toc_entries_offset(data)
    ranges: List[PatchGpuRange] = []
    max_end = 0
    for index in range(file_count):
        offset = entry_offset + index * ENTRY_RECORD_SIZE
        gpu_offset = read_u64(data, offset + 32)
        gpu_size = read_u32(data, offset + 64)
        ranges.append(PatchGpuRange(gpu_offset, gpu_size))
        max_end = max(max_end, gpu_offset + gpu_size)
    return ranges, max_end


def validate_gpu_ranges(gpu_length: int, ranges: Iterable[PatchGpuRange]) -> int:
    max_end = 0
    for item in ranges:
        if item.offset > gpu_length or item.size > gpu_length - item.offset:
            raise ContainerError(
                "the Patch TOC contains a GPU range outside the .gpu_resources file"
            )
        max_end = max(max_end, item.offset + item.size)
    return max_end


def _safe_record_name(name: str) -> str:
    if not name or len(name.encode("utf-8")) > MAX_NAME_BYTES:
        raise ContainerError("an embedded resource name is empty or too long")
    normalized = name.replace("\\", "/")
    if normalized.startswith("/") or normalized.startswith("//"):
        raise ContainerError(f"unsafe embedded resource name: {name!r}")
    parts = [part for part in normalized.split("/") if part not in ("", ".")]
    if not parts or any(part == ".." or ":" in part for part in parts):
        raise ContainerError(f"unsafe embedded resource name: {name!r}")
    return "/".join(parts)


def find_container(gpu_data: bytes) -> Optional[EmbeddedContainer]:
    if len(gpu_data) < FOOTER_SIZE:
        return None

    footer_start = len(gpu_data) - FOOTER_SIZE
    footer_magic, footer_field, footer_length, footer_digest = struct.unpack_from(
        FOOTER_FORMAT, gpu_data, footer_start
    )
    if footer_magic != FOOTER_MAGIC:
        return None
    if footer_length < HEADER_SIZE + FOOTER_SIZE or footer_length > len(gpu_data):
        raise ContainerError("the embedded container length is invalid")
    if footer_length > MAX_CONTAINER_BYTES:
        raise ContainerError("the embedded container is too large")

    start = len(gpu_data) - footer_length
    if start < 0:
        raise ContainerError("the embedded container starts before the sidecar")

    (
        magic,
        version,
        flags,
        declared_length,
        index_offset,
        index_length,
        record_count,
        _reserved,
    ) = struct.unpack_from(HEADER_FORMAT, gpu_data, start)
    if magic != CONTAINER_MAGIC:
        raise ContainerError("the EOF footer points to a container with bad magic")
    if version != CONTAINER_VERSION:
        raise ContainerError(f"unsupported embedded container version: {version}")
    if flags & ~FLAG_PREFIX_LENGTH:
        raise ContainerError(f"unsupported embedded container flags: 0x{flags:08x}")
    if declared_length != footer_length:
        raise ContainerError("header and footer container lengths disagree")
    if HEADER_SIZE + FOOTER_SIZE > footer_length:
        raise ContainerError("the embedded container is truncated")
    if index_offset < HEADER_SIZE or index_length > footer_length - FOOTER_SIZE - index_offset:
        raise ContainerError("the embedded container index is outside the container")
    if record_count > MAX_RECORDS:
        raise ContainerError("the embedded container has too many records")
    index_start = start + index_offset
    index_end = index_start + index_length
    payload_end = start + footer_length - FOOTER_SIZE
    if index_end > payload_end:
        raise ContainerError("the embedded container index overlaps its footer")

    records: List[ResourceRecord] = []
    seen_names = set()
    cursor = index_start
    for _ in range(record_count):
        if cursor + RECORD_FIXED_SIZE > index_end:
            raise ContainerError("the embedded container index is truncated")
        name_length, _record_flags, payload_offset, payload_length, expected_hash, repeated_name_length = struct.unpack_from(
            RECORD_FIXED_FORMAT, gpu_data, cursor
        )
        cursor += RECORD_FIXED_SIZE
        if name_length != repeated_name_length or name_length > MAX_NAME_BYTES:
            raise ContainerError("an embedded resource name length is invalid")
        if cursor + name_length > index_end:
            raise ContainerError("an embedded resource name exceeds the index")
        try:
            raw_name = gpu_data[cursor : cursor + name_length].decode("utf-8")
        except UnicodeDecodeError as exc:
            raise ContainerError("an embedded resource name is not UTF-8") from exc
        cursor += name_length
        name = _safe_record_name(raw_name)
        key = name.casefold()
        if key in seen_names:
            raise ContainerError(f"duplicate embedded resource name: {name}")
        seen_names.add(key)
        if payload_length > MAX_RECORD_BYTES:
            raise ContainerError(f"embedded resource is too large: {name}")
        payload_start = start + payload_offset
        payload_stop = payload_start + payload_length
        if payload_offset < index_offset + index_length or payload_stop > payload_end:
            raise ContainerError(f"embedded resource payload is outside the container: {name}")
        payload = gpu_data[payload_start:payload_stop]
        if hashlib.sha256(payload).digest() != expected_hash:
            raise ContainerError(f"embedded resource hash mismatch: {name}")
        records.append(ResourceRecord(name, payload))

    if cursor != index_end:
        raise ContainerError("the embedded container index has trailing bytes")

    digest_input = bytearray(gpu_data[start:])
    digest_input[-32:] = b"\x00" * 32
    if hashlib.sha256(digest_input).digest() != footer_digest:
        raise ContainerError("the embedded container digest does not match")

    if flags & FLAG_PREFIX_LENGTH and footer_field > start:
        raise ContainerError("the embedded container prefix length is invalid")

    return EmbeddedContainer(
        start=start,
        length=footer_length,
        version=version,
        flags=flags,
        index_offset=index_offset,
        index_length=index_length,
        records=tuple(records),
        footer_field=footer_field,
        digest=footer_digest,
    )


def build_container(records: Sequence[ResourceRecord], prefix_length: int) -> bytes:
    if not records:
        raise ContainerError("cannot create an empty embedded container")
    if len(records) > MAX_RECORDS:
        raise ContainerError("too many embedded records")
    if prefix_length < 0:
        raise ContainerError("prefix length cannot be negative")

    index = bytearray()
    index_positions: List[int] = []
    for record in records:
        name = _safe_record_name(record.name)
        name_bytes = name.encode("utf-8")
        if len(name_bytes) > 0xFFFF:
            raise ContainerError(f"embedded resource name is too long: {name}")
        if len(record.data) > MAX_RECORD_BYTES:
            raise ContainerError(f"embedded resource is too large: {name}")
        index_positions.append(len(index))
        index.extend(
            struct.pack(
                RECORD_FIXED_FORMAT,
                len(name_bytes),
                0,
                0,
                len(record.data),
                hashlib.sha256(record.data).digest(),
                len(name_bytes),
            )
        )
        index.extend(name_bytes)

    payload_start = HEADER_SIZE + len(index)
    payload = bytearray()
    for record, index_position in zip(records, index_positions):
        payload_offset = payload_start + len(payload)
        struct.pack_into("<Q", index, index_position + 4, payload_offset)
        payload.extend(record.data)

    index_offset = HEADER_SIZE
    index_length = len(index)
    footer_start = payload_start + len(payload)
    total_length = footer_start + FOOTER_SIZE

    header = struct.pack(
        HEADER_FORMAT,
        CONTAINER_MAGIC,
        CONTAINER_VERSION,
        FLAG_PREFIX_LENGTH,
        total_length,
        index_offset,
        index_length,
        len(records),
        0,
    )
    footer = struct.pack(
        FOOTER_FORMAT,
        FOOTER_MAGIC,
        prefix_length,
        total_length,
        b"\x00" * 32,
    )
    result = bytearray(header + index + payload + footer)
    digest = hashlib.sha256(result).digest()
    result[-32:] = digest
    return bytes(result)


def strip_container(gpu_data: bytes) -> Tuple[bytes, EmbeddedContainer]:
    container = find_container(gpu_data)
    if container is None:
        raise ContainerError("no recognized HD2 PhysBone container was found at EOF")

    if container.has_prefix_length:
        prefix_length = container.footer_field
        if prefix_length > container.start:
            raise ContainerError("the container prefix length exceeds its start offset")
        padding = gpu_data[prefix_length : container.start]
        if any(padding):
            raise ContainerError(
                "the alignment bytes before the container are not zero; refusing to remove them"
            )
        return gpu_data[:prefix_length], container
    # Compatibility with the earlier experimental v1 trailer, which stored
    # the aligned container start rather than the original prefix length.
    return gpu_data[: container.start], container


def _is_dsar(data: bytes) -> bool:
    return len(data) >= 4 and data[:4] == b"DSAR"


def resolve_resource(root: Path, role: str, explicit: Optional[Path]) -> Path:
    if explicit is not None:
        path = explicit.expanduser().resolve()
        if not path.is_file():
            raise ContainerError(f"resource file does not exist: {path}")
        return path
    if root is None:
        raise ContainerError(f"no source was supplied for {role}")
    root = root.expanduser().resolve()
    if not root.is_dir():
        raise ContainerError(f"resource directory does not exist: {root}")
    direct = root / role
    if direct.is_file():
        return direct
    candidates = [path for path in root.rglob(role) if path.is_file() and not path.is_symlink()]
    if len(candidates) == 1:
        return candidates[0].resolve()
    if not candidates:
        raise ContainerError(f"could not find {role!r} under {root}")
    raise ContainerError(
        f"found multiple {role!r} files under {root}; pass an explicit option"
    )


def resource_records(
    resource_dir: Optional[Path],
    lua_units: Optional[Path],
    needle: Optional[Path],
    spring_rig: Optional[Path],
) -> Tuple[ResourceRecord, ...]:
    paths = (
        ("lua_units.txt", resolve_resource(resource_dir, "lua_units.txt", lua_units)),
        ("hd2_ib_needle.bin", resolve_resource(resource_dir, "hd2_ib_needle.bin", needle)),
        ("hd2_spring_rig.bin", resolve_resource(resource_dir, "hd2_spring_rig.bin", spring_rig)),
    )
    return tuple(ResourceRecord(name, path.read_bytes()) for name, path in paths)


def copy_patch_group(source_patch: Path, destination_patch: Path, force: bool) -> None:
    destination_patch.parent.mkdir(parents=True, exist_ok=True)
    source_names = (source_patch.name, source_patch.name + ".stream", source_patch.name + ".gpu_resources")
    for name in source_names:
        source = source_patch.parent / name
        destination = destination_patch.parent / name
        if not source.exists():
            if name.endswith(".gpu_resources"):
                destination.touch()
            continue
        if destination.exists() and not force:
            raise ContainerError(f"output already exists: {destination}")
        shutil.copy2(source, destination)


def write_bytes_atomic(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_name: Optional[str] = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb", prefix=path.name + ".", suffix=".tmp", dir=str(path.parent), delete=False
        ) as handle:
            temporary_name = handle.name
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary_name, path)
        temporary_name = None
    finally:
        if temporary_name is not None:
            try:
                os.unlink(temporary_name)
            except OSError:
                pass


def destination_patch_for(source_patch: Path, output_dir: Optional[Path]) -> Path:
    if output_dir is None:
        raise ContainerError("pass --output-dir or use --in-place")
    output_dir = output_dir.expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    return output_dir / source_patch.name


def pack_command(args: argparse.Namespace) -> int:
    source_patch = Path(args.patch).expanduser().resolve()
    if not source_patch.is_file():
        raise ContainerError(f"Patch TOC does not exist: {source_patch}")
    source_gpu_path = Path(str(source_patch) + ".gpu_resources")
    source_gpu = source_gpu_path.read_bytes() if source_gpu_path.exists() else b""
    if _is_dsar(source_gpu):
        raise ContainerError(
            "the .gpu_resources sidecar is DSAR-compressed; this loose-file tool only supports raw sidecars"
        )
    ranges, _ = read_gpu_ranges(source_patch)
    validate_gpu_ranges(len(source_gpu), ranges)
    records = resource_records(
        Path(args.resource_dir).expanduser() if args.resource_dir else None,
        Path(args.lua_units).expanduser() if args.lua_units else None,
        Path(args.needle).expanduser() if args.needle else None,
        Path(args.spring_rig).expanduser() if args.spring_rig else None,
    )

    existing = find_container(source_gpu)
    if existing is not None:
        prefix, _ = strip_container(source_gpu)
    else:
        prefix = source_gpu
    container_start = align_up(len(prefix))
    container = build_container(records, len(prefix))
    output_gpu = prefix + b"\x00" * (container_start - len(prefix)) + container

    if args.in_place:
        write_bytes_atomic(source_gpu_path, output_gpu)
        destination = source_patch
    else:
        destination = destination_patch_for(source_patch, Path(args.output_dir) if args.output_dir else None)
        copy_patch_group(source_patch, destination, args.force)
        write_bytes_atomic(Path(str(destination) + ".gpu_resources"), output_gpu)

    result = {
        "operation": "pack",
        "patch": str(destination),
        "carrier": str(Path(str(destination) + ".gpu_resources")),
        "prefixLength": len(prefix),
        "containerStart": container_start,
        "containerLength": len(container),
        "carrierLength": len(output_gpu),
        "records": [{"name": r.name, "length": len(r.data), "sha256": r.sha256.hex()} for r in records],
        "tocChanged": False,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


def unpack_command(args: argparse.Namespace) -> int:
    patch = Path(args.patch).expanduser().resolve()
    gpu_path = Path(str(patch) + ".gpu_resources")
    if not gpu_path.is_file():
        raise ContainerError(f"GPU sidecar does not exist: {gpu_path}")
    container = find_container(gpu_path.read_bytes())
    if container is None:
        raise ContainerError("no recognized HD2 PhysBone container was found at EOF")
    output_dir = Path(args.output_dir).expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    written = []
    for record in container.records:
        target = output_dir.joinpath(*record.name.split("/"))
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() and not args.force:
            raise ContainerError(f"output already exists: {target}")
        write_bytes_atomic(target, record.data)
        written.append({"name": record.name, "path": str(target), "length": len(record.data)})
    print(json.dumps({"operation": "unpack", "patch": str(patch), "records": written}, ensure_ascii=False, indent=2))
    return 0


def strip_command(args: argparse.Namespace) -> int:
    source_patch = Path(args.patch).expanduser().resolve()
    source_gpu_path = Path(str(source_patch) + ".gpu_resources")
    if not source_gpu_path.is_file():
        raise ContainerError(f"GPU sidecar does not exist: {source_gpu_path}")
    stripped_gpu, container = strip_container(source_gpu_path.read_bytes())
    if args.in_place:
        write_bytes_atomic(source_gpu_path, stripped_gpu)
        destination = source_patch
    else:
        destination = destination_patch_for(source_patch, Path(args.output_dir) if args.output_dir else None)
        copy_patch_group(source_patch, destination, args.force)
        write_bytes_atomic(Path(str(destination) + ".gpu_resources"), stripped_gpu)
    print(json.dumps({
        "operation": "strip",
        "patch": str(destination),
        "removedContainerStart": container.start,
        "removedContainerLength": container.length,
        "carrierLength": len(stripped_gpu),
    }, ensure_ascii=False, indent=2))
    return 0


def inspect_command(args: argparse.Namespace) -> int:
    patch = Path(args.patch).expanduser().resolve()
    if not patch.is_file():
        raise ContainerError(f"Patch TOC does not exist: {patch}")
    gpu_path = Path(str(patch) + ".gpu_resources")
    gpu = gpu_path.read_bytes() if gpu_path.exists() else b""
    ranges, max_end = read_gpu_ranges(patch)
    max_end = validate_gpu_ranges(len(gpu), ranges)
    container = find_container(gpu)
    payload = {
        "operation": "inspect",
        "patch": str(patch),
        "gpuSidecar": str(gpu_path),
        "gpuLength": len(gpu),
        "maxTocReferencedGpuEnd": max_end,
        "unreferencedBytesAtEnd": len(gpu) - max_end,
        "container": None,
    }
    if container is not None:
        payload["container"] = {
            "start": container.start,
            "length": container.length,
            "version": container.version,
            "flags": container.flags,
            "prefixLength": container.prefix_length,
            "indexOffset": container.index_offset,
            "indexLength": container.index_length,
            "digest": container.digest.hex(),
            "records": [
                {"name": record.name, "length": len(record.data), "sha256": record.sha256.hex()}
                for record in container.records
            ],
        }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0


def add_common_output_options(parser: argparse.ArgumentParser) -> None:
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--output-dir", help="write a copied Patch group into this directory")
    group.add_argument("--in-place", action="store_true", help="replace the source .gpu_resources explicitly")
    parser.add_argument("--force", action="store_true", help="allow replacing files in --output-dir")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    inspect_parser = subparsers.add_parser("inspect", help="inspect the Patch and embedded container")
    inspect_parser.add_argument("patch")

    pack_parser = subparsers.add_parser("pack", aliases=["merge"], help="append PhysBone resources to a copied Patch group")
    pack_parser.add_argument("patch")
    pack_parser.add_argument("resource_dir", nargs="?", help="directory containing the three resource files")
    pack_parser.add_argument("--lua-units")
    pack_parser.add_argument("--needle")
    pack_parser.add_argument("--spring-rig")
    add_common_output_options(pack_parser)

    unpack_parser = subparsers.add_parser("unpack", aliases=["split"], help="extract embedded resources")
    unpack_parser.add_argument("patch")
    unpack_parser.add_argument("--output-dir", required=True)
    unpack_parser.add_argument("--force", action="store_true")

    strip_parser = subparsers.add_parser("strip", aliases=["remove"], help="remove the embedded container")
    strip_parser.add_argument("patch")
    add_common_output_options(strip_parser)

    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        if args.command == "inspect":
            return inspect_command(args)
        if args.command in ("pack", "merge"):
            return pack_command(args)
        if args.command in ("unpack", "split"):
            return unpack_command(args)
        if args.command in ("strip", "remove"):
            return strip_command(args)
        parser.error(f"unknown command: {args.command}")
        return 2
    except (ContainerError, OSError, struct.error, UnicodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
