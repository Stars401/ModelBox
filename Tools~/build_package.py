#!/usr/bin/env python3
"""modelBox 发行包构建脚本

用法:  python Tools~/build_package.py
产物:  dist~/com.unity.modelbox-v<版本>.unitypackage  (Unity: Assets > Import Package > Custom Package)
       dist~/com.unity.modelbox-v<版本>.zip           (解压到项目 Packages/ 目录)

打包范围: package.json / README.md / Editor/**（含全部 .meta，GUID 与仓库一致）
排除:     Tests（依赖测试框架）/ Documentation~（README 截图）
"""

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
        return None, uuid.uuid5(uuid.NAMESPACE_URL, "unity:" + ZIP_ROOT).hex
    with open(meta_path(rel, is_dir), "rb") as f:
        data = f.read()
    m = GUID_RE.search(data.decode("utf-8"))
    if not m:
        sys.exit(f"ERROR: {rel}.meta 中找不到 GUID")
    return data, m.group(1)


def build_unitypackage(entries, out_path):
    """GUID 目录制: <guid>/asset + <guid>/asset.meta + <guid>/pathname"""
    seen_guids = {}
    with tarfile.open(out_path, "w:gz", format=tarfile.GNU_FORMAT, compresslevel=9) as tar:

        def add(name, data):
            info = tarfile.TarInfo(name)
            info.size = len(data)
            info.mtime = int(os.path.getmtime(os.path.join(ROOT, rel) if rel else ROOT))
            tar.addfile(info, io.BytesIO(data))

        for rel, is_dir in entries:
            meta_bytes, guid = read_meta(rel, is_dir)
            if guid in seen_guids:
                sys.exit(f"ERROR: GUID 重复 {guid}: {seen_guids[guid]} vs {rel}")
            seen_guids[guid] = rel
            pathname = ASSETS_PREFIX + (("/" + rel) if rel else "")
            if meta_bytes is not None:
                add(f"{guid}/asset.meta", meta_bytes)
            add(f"{guid}/pathname", pathname.encode("utf-8"))
            if not is_dir:
                with open(os.path.join(ROOT, rel), "rb") as f:
                    add(f"{guid}/asset", f.read())
    return seen_guids


def build_zip(entries, out_path):
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for rel, is_dir in entries:
            if is_dir:
                continue
            zf.write(os.path.join(ROOT, rel), f"{ZIP_ROOT}/{rel}")
            zf.write(os.path.join(ROOT, rel + ".meta"), f"{ZIP_ROOT}/{rel}.meta")


def validate(upkg_path):
    with tarfile.open(upkg_path, "r:gz") as tar:
        names = set(tar.getnames())
        guids = {n.split("/")[0] for n in names}
        problems = []
        for g in guids:
            if f"{g}/pathname" not in names:
                problems.append(f"{g}: 缺 pathname")
            if f"{g}/asset" in names and f"{g}/asset.meta" not in names:
                problems.append(f"{g}: 有 asset 但缺 asset.meta")
        if problems:
            sys.exit("VALIDATION FAILED:\n" + "\n".join(problems))
        return len(guids)


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

    n = validate(upkg)
    for p in (upkg, zipp):
        print(f"{os.path.basename(p):44s} {os.path.getsize(p) / 1024:8.1f} KB")
    print(f"条目: {len(entries)} (文件 {len(files)}) | unitypackage GUID 目录: {n} | 结构校验通过")


if __name__ == "__main__":
    main()
