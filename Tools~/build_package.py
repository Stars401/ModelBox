#!/usr/bin/env python3
"""modelBox 发行包构建脚本

用法:  python Tools~/build_package.py
产物:  dist~/com.unity.modelbox-v<版本>.unitypackage  (Unity: Assets > Import Package > Custom Package)
       dist~/com.unity.modelbox-v<版本>.zip           (解压到项目 Packages/ 目录)

打包范围: package.json / README.md / Editor/**（含全部 .meta，GUID 与仓库一致）
排除:     Tests（依赖测试框架）/ Documentation~（README 截图）
"""

import gzip
import io
import json
import os
import re
import sys
import tarfile
import uuid
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist~")
ASSETS_PREFIX = "Assets/modelBox"          # unitypackage 导入目标路径
ZIP_ROOT = "com.unity.modelbox"            # zip 解压根目录名
GUID_RE = re.compile(r"guid:\s*([0-9a-f]{32})")


def collect_entries():
    """返回 [(relpath, is_dir)]，relpath 为仓库相对路径，'' 为包根目录。"""
    entries = [("", True)]
    for name in ("package.json", "README.md"):
        entries.append((name, False))
    for cur, dirs, files in os.walk(os.path.join(ROOT, "Editor")):
        dirs[:] = sorted(d for d in dirs if d != "~")
        rel = os.path.relpath(cur, ROOT).replace("\\", "/")
        entries.append((rel, True))
        for fn in sorted(files):
            if fn.endswith(".meta"):
                continue  # .meta 不作为独立条目，它作为 asset.meta 随主文件打包
            entries.append((rel + "/" + fn, False))
    return sorted(set(entries), key=lambda e: e[0])


def meta_path(rel, is_dir):
    return os.path.join(ROOT, rel + ".meta") if rel else None


def read_meta(rel, is_dir):
    if not rel:  # 包根目录在仓库内没有 .meta（它本身是 package 根），用确定性 GUID
        guid = uuid.uuid5(uuid.NAMESPACE_URL, "unity:" + ZIP_ROOT).hex
        data = (f"fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\n"
                "DefaultImporter:\n  externalObjects: {}\n  userData: \n"
                "  assetBundleName: \n  assetBundleVariant: \n").encode("utf-8")
        return data, guid
    with open(meta_path(rel, is_dir), "rb") as f:
        data = f.read()
    m = GUID_RE.search(data.decode("utf-8"))
    if not m:
        sys.exit(f"ERROR: {rel}.meta 中找不到 GUID")
    return data, m.group(1)


def build_unitypackage(entries, out_path):
    """GUID 目录制: <guid>/asset + <guid>/asset.meta + <guid>/pathname"""
    seen_guids = {}
    compressed = io.BytesIO()
    with gzip.GzipFile(filename="archtemp.tar", mode="wb", compresslevel=1, mtime=0,
                       fileobj=compressed) as gzip_stream, \
            tarfile.open(fileobj=gzip_stream, mode="w", format=tarfile.GNU_FORMAT) as tar:

        def add(name, data=None):
            info = tarfile.TarInfo(name)
            if data is None:
                info.type = tarfile.DIRTYPE
                info.mode = 0o755
            else:
                info.size = len(data)
            info.mtime = int(os.path.getmtime(os.path.join(ROOT, rel) if rel else ROOT))
            tar.addfile(info, io.BytesIO(data) if data is not None else None)

        for rel, is_dir in entries:
            meta_bytes, guid = read_meta(rel, is_dir)
            if guid in seen_guids:
                sys.exit(f"ERROR: GUID 重复 {guid}: {seen_guids[guid]} vs {rel}")
            seen_guids[guid] = rel
            pathname = ASSETS_PREFIX + (("/" + rel) if rel else "")
            add(guid)
            add(f"{guid}/asset.meta", meta_bytes)
            add(f"{guid}/pathname", pathname.encode("utf-8"))
            if not is_dir:
                with open(os.path.join(ROOT, rel), "rb") as f:
                    add(f"{guid}/asset", f.read())

    # Match Unity's native gzip wrapper, including its FAT OS marker.
    data = bytearray(compressed.getvalue())
    data[9] = 0
    with open(out_path, "wb") as f:
        f.write(data)
    return seen_guids


def build_zip(entries, out_path):
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for rel, is_dir in entries:
            zf.write(os.path.join(ROOT, rel), f"{ZIP_ROOT}/{rel}")
            if rel:
                zf.write(os.path.join(ROOT, rel + ".meta"), f"{ZIP_ROOT}/{rel}.meta")


def expected_members(entries, unitypackage):
    expected = {}
    seen_guids = {}
    for rel, is_dir in entries:
        meta_bytes, guid = read_meta(rel, is_dir)
        if guid in seen_guids:
            sys.exit(f"ERROR: GUID 重复 {guid}: {seen_guids[guid]} vs {rel}")
        seen_guids[guid] = rel
        if unitypackage:
            expected[guid] = None
            expected[f"{guid}/asset.meta"] = meta_bytes
            pathname = ASSETS_PREFIX + (("/" + rel) if rel else "")
            expected[f"{guid}/pathname"] = pathname.encode("utf-8")
            asset_name = f"{guid}/asset"
        else:
            asset_name = f"{ZIP_ROOT}/{rel}"
            if is_dir:
                expected[asset_name.rstrip("/") + "/"] = None
            if rel:
                expected[f"{ZIP_ROOT}/{rel}.meta"] = meta_bytes
        if not is_dir:
            with open(os.path.join(ROOT, rel), "rb") as f:
                expected[asset_name] = f.read()
    return expected


def validate_members(expected, members):
    """None 表示真实目录；普通文件必须与源字节完全一致。"""
    seen = set()
    problems = []
    for name, data in members:
        if name in seen:
            problems.append(f"{name}: 归档条目重复")
        seen.add(name)
        if name not in expected:
            problems.append(f"{name}: 非预期条目")
        elif data != expected[name]:
            problems.append(f"{name}: 内容或类型与源数据不一致")
    for name in sorted(expected.keys() - seen):
        problems.append(f"{name}: 缺少条目")
    if problems:
        sys.exit("VALIDATION FAILED:\n" + "\n".join(problems))


def validate(upkg_path, entries=None):
    entries = collect_entries() if entries is None else entries
    expected = expected_members(entries, unitypackage=True)
    with tarfile.open(upkg_path, "r:gz") as tar:
        validate_members(expected, ((member.name, tar.extractfile(member).read() if member.isfile() else None)
                                    for member in tar.getmembers()))
    return len(entries)


def validate_zip(zip_path, entries=None):
    entries = collect_entries() if entries is None else entries
    expected = expected_members(entries, unitypackage=False)
    with zipfile.ZipFile(zip_path) as zf:
        validate_members(expected, ((member.filename, None if member.is_dir() else zf.read(member))
                                    for member in zf.infolist()))
    return len(entries)


def main():
    os.makedirs(DIST, exist_ok=True)
    with open(os.path.join(ROOT, "package.json"), encoding="utf-8") as f:
        version = json.load(f)["version"]
    entries = collect_entries()
    files = [e for e in entries if not e[1]]

    upkg = os.path.join(DIST, f"{ZIP_ROOT}-v{version}.unitypackage")
    zipp = os.path.join(DIST, f"{ZIP_ROOT}-v{version}.zip")
    build_unitypackage(entries, upkg)
    build_zip(entries, zipp)

    n = validate(upkg, entries)
    validate_zip(zipp, entries)
    for p in (upkg, zipp):
        print(f"{os.path.basename(p):44s} {os.path.getsize(p) / 1024:8.1f} KB")
    print(f"条目: {len(entries)} (文件 {len(files)}) | unitypackage GUID 目录: {n} | 两归档结构、源字节及 GUID 校验通过")


if __name__ == "__main__":
    main()
