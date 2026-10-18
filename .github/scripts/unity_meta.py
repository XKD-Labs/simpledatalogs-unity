#!/usr/bin/env python3
"""Writes or checks the Unity .meta files a UPM git package needs.

Usage: unity_meta.py write   (adds missing .meta files with stable GUIDs)
       unity_meta.py check   (fails on a missing, orphaned or duplicate .meta)
"""

import hashlib
import pathlib
import re
import sys

# The package root is two folders up from this script.
PACKAGE_ROOT = pathlib.Path(__file__).resolve().parents[2]

# GUIDs are derived from this name plus the path, so they stay stable across machines.
GUID_NAMESPACE = "com.xkdlabs.simpledatalogs:"

# Folders whose contents Unity never imports and that ship no .meta files at all.
EXCLUDED_FOLDERS = {"Tests~"}

# The importer block for each kind of asset.
FOLDER_IMPORTER = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
SCRIPT_IMPORTER = (
    "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n"
    "  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
)
ASSEMBLY_IMPORTER = "AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
MANIFEST_IMPORTER = "PackageManifestImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
TEXT_IMPORTER = "TextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
DEFAULT_IMPORTER = "DefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"


def is_skipped(relative_path):
    """True for hidden files, excluded folders and .meta files themselves."""
    parts = relative_path.parts
    if any(part.startswith(".") for part in parts):
        return True
    if any(part in EXCLUDED_FOLDERS for part in parts):
        return True
    return relative_path.suffix == ".meta"


def needs_meta(relative_path):
    """Folders ending in ~ (e.g. Samples~) have no .meta of their own, but their contents do."""
    return not relative_path.name.endswith("~")


def importer_for(asset_path):
    """Picks the importer block Unity would write for this asset."""
    if asset_path.is_dir():
        return FOLDER_IMPORTER
    if asset_path.name == "package.json":
        return MANIFEST_IMPORTER
    if asset_path.suffix == ".cs":
        return SCRIPT_IMPORTER
    if asset_path.suffix == ".asmdef":
        return ASSEMBLY_IMPORTER
    if asset_path.suffix in {".md", ".txt", ".json", ".xml"}:
        return TEXT_IMPORTER
    return DEFAULT_IMPORTER


def stable_guid(relative_path):
    """A 32-character lowercase hex GUID from the package-relative path."""
    return hashlib.md5((GUID_NAMESPACE + relative_path.as_posix()).encode("utf-8")).hexdigest()


def list_assets():
    """Every package file and folder that needs a .meta, sorted."""
    assets = []
    for asset_path in sorted(PACKAGE_ROOT.rglob("*")):
        relative_path = asset_path.relative_to(PACKAGE_ROOT)
        if is_skipped(relative_path) or not needs_meta(relative_path):
            continue
        assets.append(asset_path)
    return assets


def write_missing_metas():
    """Adds a .meta beside every asset that lacks one."""
    written_count = 0
    for asset_path in list_assets():
        meta_path = asset_path.with_name(asset_path.name + ".meta")
        if meta_path.exists():
            continue
        relative_path = asset_path.relative_to(PACKAGE_ROOT)
        meta_path.write_text(f"fileFormatVersion: 2\nguid: {stable_guid(relative_path)}\n{importer_for(asset_path)}")
        written_count += 1
    print(f"Wrote {written_count} .meta files")


def check_metas():
    """Fails on a missing .meta, an orphaned .meta, or a malformed or duplicate GUID."""
    problems = []
    assets = list_assets()

    # Every asset has a .meta.
    for asset_path in assets:
        if not asset_path.with_name(asset_path.name + ".meta").exists():
            problems.append(f"missing .meta: {asset_path.relative_to(PACKAGE_ROOT)}")

    # Every .meta belongs to an asset, and GUIDs are well formed and unique.
    asset_set = set(assets)
    guid_owners = {}
    for meta_path in sorted(PACKAGE_ROOT.rglob("*.meta")):
        relative_meta = meta_path.relative_to(PACKAGE_ROOT)
        if any(part.startswith(".") or part in EXCLUDED_FOLDERS for part in relative_meta.parts):
            continue
        owner_path = meta_path.with_name(meta_path.name[: -len(".meta")])
        if owner_path not in asset_set:
            problems.append(f"orphaned .meta: {relative_meta}")
        guid_match = re.search(r"^guid: ([0-9a-f]{32})$", meta_path.read_text(), re.MULTILINE)
        if not guid_match:
            problems.append(f"malformed guid: {relative_meta}")
            continue
        guid = guid_match.group(1)
        if guid in guid_owners:
            problems.append(f"duplicate guid {guid}: {relative_meta} and {guid_owners[guid]}")
        guid_owners[guid] = relative_meta

    for problem in problems:
        print(problem)
    print(f"Checked {len(assets)} assets: {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "check"
    if mode == "write":
        write_missing_metas()
        sys.exit(0)
    sys.exit(check_metas())
