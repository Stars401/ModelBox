#!/usr/bin/env python3
"""Packaging regression tests. All generated files stay in dist~/qa-packaging."""

import importlib.util
import io
from pathlib import Path
import re
import tarfile
import unittest
from unittest.mock import patch
import uuid
import zipfile


REPO = Path(__file__).resolve().parent.parent
QA = REPO / "dist~" / "qa-packaging"
SPEC = importlib.util.spec_from_file_location("build_package", Path(__file__).with_name("build_package.py"))
build_package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build_package)


class PackagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = QA / "fixture-source"
        (cls.source / "Editor" / "Empty").mkdir(parents=True, exist_ok=True)
        for index, relative in enumerate(("package.json", "README.md", "Editor", "Editor/Empty", "Editor/Sample.cs"), 1):
            path = cls.source / relative
            if not path.is_dir():
                path.write_bytes(b'{"version":"0.0.0"}\n' if relative == "package.json" else b"source\r\n\x00\xff\n")
            folder = "folderAsset: yes\n" if path.is_dir() else ""
            Path(str(path) + ".meta").write_bytes(
                (f"fileFormatVersion: 2\nguid: {index:032x}\n{folder}DefaultImporter:\n  externalObjects: {{}}\n").encode("ascii"))

    def setUp(self):
        self.root_patch = patch.object(build_package, "ROOT", str(self.source))
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)
        self.entries = build_package.collect_entries()
        self.unitypackage = QA / (self._testMethodName + ".unitypackage")
        self.zip_path = QA / (self._testMethodName + ".zip")
        build_package.build_unitypackage(self.entries, self.unitypackage)
        build_package.build_zip(self.entries, self.zip_path)

    def rewrite_unitypackage(self, *, changes=None, remove=(), extra=()):
        output = QA / (self._testMethodName + "-mutated.unitypackage")
        with tarfile.open(self.unitypackage, "r:gz") as source, tarfile.open(output, "w:gz") as target:
            for member in source.getmembers():
                if member.name in remove:
                    continue
                data = source.extractfile(member).read()
                data = (changes or {}).get(member.name, data)
                member.size = len(data)
                target.addfile(member, io.BytesIO(data))
            for name, data in extra:
                member = tarfile.TarInfo(name)
                member.size = len(data)
                target.addfile(member, io.BytesIO(data))
        return output

    def rewrite_zip(self, *, changes=None, remove=(), extra=()):
        output = QA / (self._testMethodName + "-mutated.zip")
        with zipfile.ZipFile(self.zip_path) as source, zipfile.ZipFile(output, "w") as target:
            for member in source.infolist():
                if member.filename not in remove:
                    target.writestr(member, (changes or {}).get(member.filename, source.read(member)))
            for name, data in extra:
                target.writestr(name, data)
        return output

    def test_zip_includes_directory_metadata_and_empty_directories(self):
        with zipfile.ZipFile(self.zip_path) as archive:
            for relative, is_directory in self.entries:
                if is_directory:
                    self.assertIn(build_package.ZIP_ROOT + "/" + (relative + "/" if relative else ""), archive.namelist())
                if relative:
                    name = build_package.ZIP_ROOT + "/" + relative + ".meta"
                    self.assertIn(name, archive.namelist())
                    self.assertEqual(Path(str(self.source / relative) + ".meta").read_bytes(), archive.read(name))

    def test_unitypackage_root_has_deterministic_folder_metadata(self):
        guid = uuid.uuid5(uuid.NAMESPACE_URL, "unity:" + build_package.ZIP_ROOT).hex
        with tarfile.open(self.unitypackage) as archive:
            self.assertIn(guid + "/asset.meta", archive.getnames())
            meta = archive.extractfile(guid + "/asset.meta").read()
            self.assertIn(("guid: " + guid).encode("ascii"), meta)
            self.assertIn(b"folderAsset: yes", meta)

    def test_unitypackage_validator_rejects_changed_asset_bytes(self):
        path = "00000000000000000000000000000005/asset"
        output = self.rewrite_unitypackage(changes={path: b"changed"})
        with self.assertRaises(SystemExit):
            build_package.validate(output)

    def test_unitypackage_validator_rejects_changed_guid(self):
        path = "00000000000000000000000000000005/asset.meta"
        output = self.rewrite_unitypackage(changes={path: b"fileFormatVersion: 2\nguid: ffffffffffffffffffffffffffffffff\n"})
        with self.assertRaises(SystemExit):
            build_package.validate(output)

    def test_unitypackage_validator_requires_folder_metadata(self):
        output = self.rewrite_unitypackage(remove=("00000000000000000000000000000003/asset.meta",))
        with self.assertRaises(SystemExit):
            build_package.validate(output)

    def test_unitypackage_validator_requires_asset_and_correct_pathname(self):
        path = "00000000000000000000000000000005/asset"
        for options in ({"remove": (path,)}, {"changes": {path.replace("asset", "pathname"): b"Assets/wrong"}}):
            with self.subTest(options=options):
                output = self.rewrite_unitypackage(**options)
                with self.assertRaises(SystemExit):
                    build_package.validate(output)

    def test_unitypackage_validator_rejects_extra_and_duplicate_members(self):
        path = "00000000000000000000000000000005/pathname"
        for extra in (((path, b"Assets/modelBox/Editor/Sample.cs"),), (("unexpected/pathname", b"Assets/unexpected"),)):
            with self.subTest(extra=extra):
                output = self.rewrite_unitypackage(extra=extra)
                with self.assertRaises(SystemExit):
                    build_package.validate(output)

    def test_zip_validator_checks_structure_source_bytes_and_guids(self):
        validator = getattr(build_package, "validate_zip", None)
        self.assertTrue(callable(validator), "Both release formats need validation.")
        root = build_package.ZIP_ROOT + "/"
        cases = (
            {"remove": (root + "Editor.meta",)},
            {"changes": {root + "Editor/Sample.cs": b"changed"}},
            {"changes": {root + "Editor/Sample.cs.meta": b"guid: ffffffffffffffffffffffffffffffff\n"}},
            {"extra": ((root + "unexpected", b"unexpected"),)},
            {"extra": ((root + "README.md", b"duplicate"),)},
        )
        for options in cases:
            with self.subTest(options=options):
                with self.assertWarns(UserWarning) if "extra" in options and options["extra"][0][0].endswith("README.md") else self.subTest():
                    output = self.rewrite_zip(**options)
                with self.assertRaises(SystemExit):
                    validator(output)

    def test_valid_archives_pass_both_validators(self):
        self.assertEqual(len(self.entries), build_package.validate(self.unitypackage))
        validator = getattr(build_package, "validate_zip", None)
        self.assertTrue(callable(validator), "Both release formats need validation.")
        self.assertEqual(len(self.entries), validator(self.zip_path))

    def test_repository_archives_preserve_source_bytes_and_guids(self):
        with patch.object(build_package, "ROOT", str(REPO)):
            entries = build_package.collect_entries()
            unitypackage = QA / "current-source.unitypackage"
            zip_path = QA / "current-source.zip"
            build_package.build_unitypackage(entries, unitypackage)
            build_package.build_zip(entries, zip_path)
            with tarfile.open(unitypackage) as tar, zipfile.ZipFile(zip_path) as zip_archive:
                for relative, is_directory in entries:
                    if not relative:
                        continue
                    meta = Path(str(REPO / relative) + ".meta").read_bytes()
                    guid = re.search(rb"guid:\s*([0-9a-f]{32})", meta).group(1).decode("ascii")
                    self.assertEqual(meta, tar.extractfile(guid + "/asset.meta").read())
                    self.assertIn(build_package.ZIP_ROOT + "/" + relative + ".meta", zip_archive.namelist())
                    self.assertEqual(meta, zip_archive.read(build_package.ZIP_ROOT + "/" + relative + ".meta"))
                    if not is_directory:
                        source = (REPO / relative).read_bytes()
                        self.assertEqual(source, tar.extractfile(guid + "/asset").read())
                        self.assertEqual(source, zip_archive.read(build_package.ZIP_ROOT + "/" + relative))


if __name__ == "__main__":
    unittest.main()
