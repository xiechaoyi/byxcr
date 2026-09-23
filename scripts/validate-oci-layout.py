#!/usr/bin/env python3
"""校验 byxcr 产出的 OCI Image Layout 归档（.tar，内容可能是 gzip 压缩流；已解开的目录亦可）。

检查项：
  1. oci-layout 与 index.json 可解析，imageLayoutVersion == "1.0.0"
  2. 根索引 index.json 恰好一条 manifest（podman load / docker load 直接导入的前提）：
     单平台时直接指向镜像清单；多平台时指向一个「内层镜像索引」，平台在其 manifests 中展开
  3. blobs/sha256/<hex> 的文件内容 sha256 == 文件名
  4. 各平台 manifest 的 config / layers 全部存在，platform 信息完整
  5. config 的 rootfs.diff_ids 与 layers 一一对应：
       - 压缩层（gzip magic）：解压后的 sha256 == diff_ids[i]
       - 未压缩层：blob 摘要本身 == diff_ids[i]
     （注意：压缩层的 blob 摘要 != diff_ids[i]，前者是压缩后的，不能直接比）
  6. docker-archive 兼容清单 manifest.json（若存在）：Config / Layers 指向的路径在归档内存在，
     Layers 条数与 config 的 rootfs.diff_ids 一致、顺序对齐，RepoTags 形如 repo:tag。
     这是传统 docker（graph driver）`docker load` 的入口；缺了它 docker 会报
     `open .../manifest.json: no such file or directory`。

用法：
    python scripts/validate-oci-layout.py <归档文件或已解开目录> [更多路径...]
"""

from __future__ import annotations

import gzip
import hashlib
import io
import json
import os
import sys
import tarfile

GZIP_MAGIC = b"\x1f\x8b"
OCI_INDEX_MEDIA_TYPE = "application/vnd.oci.image.index.v1+json"
REF_NAME_ANNOTATION = "org.opencontainers.image.ref.name"


class ValidationError(Exception):
    pass


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_blob(store: dict[str, bytes], digest: str) -> bytes:
    algo, _, hexdigest = digest.partition(":")
    if algo != "sha256":
        raise ValidationError(f"不支持的摘要算法：{digest}")
    if hexdigest not in store:
        raise ValidationError(f"blob 缺失：{digest}")
    return store[hexdigest]


def load_archive(path: str) -> dict[str, bytes]:
    """把归档读成 {相对路径: 内容}；支持 gzip 压缩或未压缩的 tar、以及已解开的目录。"""
    if os.path.isdir(path):
        store: dict[str, bytes] = {}
        for root, _dirs, files in os.walk(path):
            for name in files:
                full = os.path.join(root, name)
                with open(full, "rb") as fh:
                    store[os.path.relpath(full, path).replace(os.sep, "/")] = fh.read()
        return store

    with open(path, "rb") as fh:
        raw = fh.read()
    if raw[:2] == GZIP_MAGIC:
        raw = gzip.decompress(raw)

    store = {}
    with tarfile.open(fileobj=io.BytesIO(raw)) as tar:
        for member in tar.getmembers():
            if not member.isfile():
                continue
            extracted = tar.extractfile(member)
            if extracted is None:
                continue
            store[member.name.lstrip("./")] = extracted.read()
    return store


def resolve_platform_descriptors(blobs: dict[str, bytes], root_manifest: dict) -> tuple[list[dict], str]:
    """把根索引里唯一的一条 descriptor 展开成「各平台清单 descriptor」列表。

    单平台：根 descriptor 本身就是镜像清单（带 platform）。
    多平台：根 descriptor 指向一个内层镜像索引（mediaType = image.index），平台在其 manifests 里。
    """
    digest = root_manifest.get("digest")
    if not digest:
        raise ValidationError("根索引的 manifest descriptor 缺少 digest")

    tag = (root_manifest.get("annotations") or {}).get(REF_NAME_ANNOTATION, "-")
    target = json.loads(read_blob(blobs, digest))

    if target.get("mediaType") == OCI_INDEX_MEDIA_TYPE or "manifests" in target:
        inner = target.get("manifests") or []
        if not inner:
            raise ValidationError("内层镜像索引的 manifests 为空")
        print(f"  根索引 → 内层索引 {digest[:19]}…（{len(inner)} 个平台，tag={tag}）")
        return inner, tag

    platform = root_manifest.get("platform") or {}
    if not platform.get("architecture") or not platform.get("os"):
        raise ValidationError("单平台归档的根 descriptor 缺少 platform.architecture/os")
    return [root_manifest], tag


def validate(path: str) -> None:
    store = load_archive(path)

    # 1) oci-layout
    if "oci-layout" not in store:
        raise ValidationError("缺少 oci-layout 文件")
    layout = json.loads(store["oci-layout"])
    if layout.get("imageLayoutVersion") != "1.0.0":
        raise ValidationError(f"imageLayoutVersion 异常：{layout.get('imageLayoutVersion')}")

    # 2) index.json：根索引里必须恰好一条 manifest
    if "index.json" not in store:
        raise ValidationError("缺少 index.json")
    index = json.loads(store["index.json"])
    root_manifests = index.get("manifests") or []
    if not root_manifests:
        raise ValidationError("index.json 的 manifests 为空")
    if len(root_manifests) != 1:
        raise ValidationError(
            f"index.json 里有 {len(root_manifests)} 条 manifest —— "
            "podman load / docker load 会报「more than one image in oci, choose an image」，"
            "多平台应合并成一个内层镜像索引"
        )

    blobs = {
        name.split("/")[-1]: content
        for name, content in store.items()
        if name.startswith("blobs/sha256/")
    }
    if not blobs:
        raise ValidationError("没有找到任何 blobs/sha256/*")

    # 3) blob 内容 == 文件名
    for hexdigest, content in blobs.items():
        actual = sha256(content)
        if actual != hexdigest:
            raise ValidationError(f"blob 摘要不符：blobs/sha256/{hexdigest} 实际为 {actual}")

    manifests, tag = resolve_platform_descriptors(blobs, root_manifests[0])

    platforms: list[str] = []
    total_layers = 0
    layout_layers: dict[str, list[str]] = {}  # config 摘要(hex) → 该平台图层 blob 摘要(hex) 顺序表

    for descriptor in manifests:
        digest = descriptor.get("digest")
        if not digest:
            raise ValidationError("平台清单 descriptor 缺少 digest")

        platform = descriptor.get("platform") or {}
        arch = platform.get("architecture")
        os_name = platform.get("os")
        variant = platform.get("variant")
        if not arch or not os_name:
            raise ValidationError(f"manifest {digest} 缺少 platform.architecture/os")
        label = f"{os_name}/{arch}" + (f"/{variant}" if variant else "")
        platforms.append(label)

        manifest = json.loads(read_blob(blobs, digest))
        config_descriptor = manifest.get("config")
        if not config_descriptor:
            raise ValidationError(f"manifest {digest} 缺少 config")

        config_digest = config_descriptor["digest"]
        config = json.loads(read_blob(blobs, config_digest))

        diff_ids = (config.get("rootfs") or {}).get("diff_ids") or []
        layers = manifest.get("layers") or []
        layout_layers[config_digest.split(":", 1)[1]] = [layer["digest"].split(":", 1)[1] for layer in layers]
        if len(diff_ids) != len(layers):
            raise ValidationError(
                f"{label}: rootfs.diff_ids 有 {len(diff_ids)} 项，layers 有 {len(layers)} 项，数量不一致"
            )

        for i, layer in enumerate(layers):
            layer_bytes = read_blob(blobs, layer["digest"])
            expected = diff_ids[i]

            if layer_bytes[:2] == GZIP_MAGIC:
                try:
                    layer_bytes = gzip.decompress(layer_bytes)
                except OSError as ex:
                    raise ValidationError(f"{label}: 第 {i} 层解压失败：{ex}") from ex

            actual = "sha256:" + sha256(layer_bytes)
            if actual != expected:
                hint = "（该层未压缩，应直接比对 blob 摘要）" if layer["digest"] == expected else ""
                raise ValidationError(
                    f"{label}: 第 {i} 层解压后摘要 {actual} != diff_ids {expected}{hint}"
                )
            total_layers += 1

        print(
            f"  ✓ {label:<16} manifest={digest[:19]}… "
            f"config={config_digest[:19]}… layers={len(layers)}"
        )

    docker_note = validate_docker_manifest(store, layout_layers, tag)

    size = sum(len(v) for v in store.values())
    print(
        f"OK  {path}\n"
        f"    tag={tag}｜平台 {len(manifests)} 个：{', '.join(platforms)}\n"
        f"    blob {len(blobs)} 个（其中图层 {total_layers} 个），解包后 {size / 1024 / 1024:.1f} MiB\n"
        f"    {docker_note}"
    )


def validate_docker_manifest(store: dict[str, bytes], layout_layers: dict[str, list[str]], tag: str) -> str:
    """校验 docker-archive 兼容清单 manifest.json —— 传统 docker（graph driver）的 docker load 入口。

    检查 Config / Layers 指向的文件是否都在归档内，且 Layers 顺序与「对应平台清单」的图层顺序一致
    （docker 按顺序套用图层，错序会套出错误的文件系统），RepoTags 是否为 repo:tag。
    """
    if "manifest.json" not in store:
        return "docker 兼容清单：无（传统 docker 的 docker load 会报 manifest.json: no such file or directory）"

    items = json.loads(store["manifest.json"])
    if not isinstance(items, list) or not items:
        raise ValidationError("manifest.json 不是非空数组")

    summary: list[str] = []
    for index, item in enumerate(items):
        if not isinstance(item, dict):
            raise ValidationError(f"manifest.json 第 {index} 项不是 JSON 对象")

        config_path = item.get("Config") or ""
        if config_path not in store:
            raise ValidationError(f"manifest.json 第 {index} 项的 Config 在归档内不存在：{config_path!r}")

        expected = layout_layers.get(config_path.split("/")[-1])
        if expected is None:
            raise ValidationError(f"manifest.json 第 {index} 项的 Config {config_path} 不对应任何平台清单")

        layers = item.get("Layers") or []
        if len(layers) != len(expected):
            raise ValidationError(
                f"manifest.json 第 {index} 项 Layers 有 {len(layers)} 项，对应平台清单有 {len(expected)} 层"
                " —— docker 会报 invalid manifest, layers length mismatch"
            )

        for i, layer_path in enumerate(layers):
            if layer_path not in store:
                raise ValidationError(f"manifest.json 第 {index} 项的 Layers[{i}] 在归档内不存在：{layer_path!r}")
            if layer_path.split("/")[-1] != expected[i]:
                raise ValidationError(
                    f"manifest.json 第 {index} 项 Layers[{i}]={layer_path} 与该平台第 {i} 层 {expected[i]} "
                    "不一致（图层顺序错位）"
                )

        tags = item.get("RepoTags") or []
        if not tags:
            raise ValidationError(f"manifest.json 第 {index} 项没有 RepoTags，docker load 只会得到无名镜像")
        for repo_tag in tags:
            name, sep, version = repo_tag.rpartition(":")
            if not sep or not name or not version:
                raise ValidationError(f"manifest.json 第 {index} 项的 RepoTags 不是 repo:tag：{repo_tag!r}")

        summary.append(f"{tags[0]}（config={config_path}｜layers={len(layers)}）")

    note = "docker 兼容清单：" + "；".join(summary)
    if tag != "-" and not any(
        repo_tag.rpartition(":")[2] == tag for item in items for repo_tag in (item.get("RepoTags") or [])
    ):
        note += f"（注意：RepoTags 的 tag 与根索引 tag={tag} 不一致）"
    return note


def main(argv: list[str]) -> int:
    targets = argv[1:]
    if not targets:
        print(__doc__)
        return 2

    failed = 0
    for target in targets:
        print(f"== {target}")
        try:
            validate(target)
        except (ValidationError, json.JSONDecodeError, tarfile.TarError, OSError) as ex:
            print(f"FAIL {ex}")
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
