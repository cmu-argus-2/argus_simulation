"""Print the semantic graph delta and material diff against Cesium 1.25.1.

Run from any directory with Python 3; Unity must have resolved its packages first.
This is a source review aid, not a GPU or geographic acceptance test.
"""

import argparse
import difflib
import hashlib
import json
from pathlib import Path


def objects(path):
    source = path.read_text()
    decoder = json.JSONDecoder()
    result = {}
    offset = 0
    while offset < len(source):
        if source[offset].isspace():
            offset += 1
            continue
        value, offset = decoder.raw_decode(source, offset)
        key = value["m_ObjectId"]
        if key in result:
            raise ValueError(f"Duplicate graph object: {key}")
        result[key] = value
    return result


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, help="Cesium 1.25.1 package directory")
    args = parser.parse_args()
    candidates = [args.package] if args.package else list(
        (root / "Library/PackageCache").glob("com.cesium.unity@*")
    )
    candidates = [p for p in candidates
                  if json.loads((p / "package.json").read_text())["version"] == "1.25.1"]
    if len(candidates) != 1:
        raise ValueError("Resolve packages in Unity or supply --package for Cesium 1.25.1")
    package = candidates[0]
    upstream = package / "Source/Runtime/Resources"
    local = root / "Assets/ArgusSimulation/Materials"
    graph = upstream / "CesiumDefaultTilesetShader.shadergraph"
    digest = hashlib.sha256(graph.read_text().encode()).hexdigest()
    if digest != "8f605479c042249d9159da17edd55802cf86b0525a36fdc9173fb65c90ecbd32":
        raise ValueError("Upstream graph differs from the reviewed baseline")
    before, after = objects(graph), objects(local / "ArgusFourOverlayTileset.shadergraph")
    removed = before.keys() - after.keys()
    if removed:
        raise ValueError(f"Removed upstream objects: {sorted(removed)}")
    changed = [k for k in before if before[k] != after[k]]
    added = sorted(after.keys() - before.keys())
    print(f"Cesium 1.25.1 graph SHA-256 (LF): {digest}")
    print(f"Upstream objects: {len(before)}; unchanged: {len(before) - len(changed)}")
    print(f"Changed: {len(changed)}; added: {len(added)}; removed: 0")
    for key in changed:
        old, new = before[key], after[key]
        fields = [f for f in sorted(old.keys() | new.keys()) if old.get(f) != new.get(f)]
        print(f"\nCHANGED {old['m_Type']} ({key})")
        for field in fields:
            a, b = old.get(field), new.get(field)
            if isinstance(a, list) and isinstance(b, list):
                for sign, values, other in [("-", a, b), ("+", b, a)]:
                    for value in values:
                        if value not in other:
                            print(f"  {sign} {field}: {json.dumps(value, sort_keys=True)}")
            else:
                print(f"  {field}: {a!r} -> {b!r}")
    print("\nADDED OBJECTS (inspect these IDs in the graph if needed)")
    for key in added:
        obj = after[key]
        print(f"  {key}: {obj['m_Type']} {obj.get('m_Name', '')}")
    print("\nMATERIAL DIFF (trailing whitespace ignored)")
    old = (upstream / "CesiumDefaultTilesetMaterial.mat").read_text().splitlines()
    new = (local / "ArgusFourOverlayTileset.mat").read_text().splitlines()
    print("\n".join(difflib.unified_diff(
        [s.rstrip() for s in old], [s.rstrip() for s in new],
        fromfile="Cesium 1.25.1 default material", tofile="Argus four-overlay material", n=2)))
    if (package / "LICENSE").read_text() != (local / "Cesium-LICENSE.txt").read_text():
        raise ValueError("Bundled license differs from upstream (ignoring line endings)")
    print("\nBundled license matches upstream (line endings normalized).")


if __name__ == "__main__":
    main()
