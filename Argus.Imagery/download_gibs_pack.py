#!/usr/bin/env python3
"""Download a full-resolution NASA GIBS imagery pack for offline simulator runs.

The pack holds exactly the tiles Cesium requests from NASA GIBS: the same WMS
requests, the same 512 px geographic tiling (2 x 1 root tiles), levels 0 through
the simulator's maximum level. Unity reads it when NasaGibsRasterController has
"Use Imagery Pack" enabled or the player is started with -gt-pack <directory>.

Layout, which NasaGibsRasterController depends on:

    <out>/<layer>/<YYYY-MM-DD | static>/<z>/<x>/<reverseY>.<png | jpg>
    <out>/<layer>/<YYYY-MM-DD | static>/manifest.json

reverseY counts rows from the north, matching Cesium's {reverseY} placeholder.
The download is resumable: rerun the same command and finished tiles are skipped.
manifest.json is written with "complete": true only when every tile succeeded.
"""

import argparse
import concurrent.futures
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

WMS_ENDPOINT = "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi"
DEFAULT_LAYER = "VIIRS_SNPP_CorrectedReflectance_TrueColor"
BASE_LAYER = "BlueMarble_NextGeneration"
STATIC_DATE = "static"
DEFAULT_DATE = "2025-06-21"
TILE_SIZE = 512
MAX_LEVEL = 8
USER_AGENT = "argus-simulation-imagery-pack/1.0"

# Mean KB per tile by level, sampled from GIBS in September 2026 (VIIRS true color
# on 2025-06-21 and Blue Marble). Levels below 4 use the level-4 value.
ESTIMATED_KB_PER_TILE = {
    "png": {8: 122, 7: 268, 6: 388, 5: 420, 4: 366},
    "jpeg": {8: 14, 7: 22, 6: 36, 5: 42, 4: 43},
    "base": {8: 9, 7: 8, 6: 12, 5: 14, 4: 20},
}

EXTENSIONS = {"png": "png", "jpeg": "jpg"}


def tile_count(level):
    return 2 ** (2 * level + 1)


def estimate_bytes(kind, max_level):
    per_level = ESTIMATED_KB_PER_TILE[kind]
    return sum(
        tile_count(level) * per_level.get(level, per_level[4]) * 1024
        for level in range(max_level + 1))


def iterate_tiles(max_level):
    for level in range(max_level + 1):
        rows = 2 ** level
        size = 180.0 / rows
        for reverse_y in range(rows):
            north = 90.0 - reverse_y * size
            for x in range(2 * rows):
                west = -180.0 + x * size
                yield level, x, reverse_y, (west, north - size, west + size, north)


def build_url(layer, date, image_format, bbox):
    # Mirrors NasaGibsUrl.BuildBaseTemplate / BuildTemplate so the pixels are identical.
    parts = [
        "SERVICE=WMS", "REQUEST=GetMap", "VERSION=1.1.1",
        f"LAYERS={layer}", "STYLES=default",
    ]
    if date == STATIC_DATE:
        parts.append("FORMAT=image/jpeg")
    elif image_format == "png":
        parts += ["FORMAT=image/png", "TRANSPARENT=TRUE", f"TIME={date}"]
    else:
        parts += ["FORMAT=image/jpeg", f"TIME={date}"]
    west, south, east, north = bbox
    parts += [
        "SRS=EPSG:4326",
        f"BBOX={west!r},{south!r},{east!r},{north!r}",
        f"WIDTH={TILE_SIZE}", f"HEIGHT={TILE_SIZE}",
    ]
    return WMS_ENDPOINT + "?" + "&".join(parts)


def fetch(url, attempts=5):
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                body = response.read()
                # GIBS reports WMS errors as XML with HTTP 200.
                if not response.headers.get("Content-Type", "").startswith("image/"):
                    raise ValueError(body[:200].decode("utf-8", "replace"))
                return body
        except (urllib.error.URLError, TimeoutError, ValueError, ConnectionError):
            if attempt == attempts - 1:
                raise
            time.sleep(2 ** attempt)
    raise AssertionError("unreachable")


def download_layer(out_root, layer, date, image_format, max_level, workers):
    extension = "jpg" if date == STATIC_DATE else EXTENSIONS[image_format]
    layer_dir = out_root / layer / date
    total = sum(tile_count(level) for level in range(max_level + 1))
    print(f"\n{layer} {date}: {total:,} tiles -> {layer_dir}")

    lock = threading.Lock()
    state = {"done": 0, "bytes": 0, "failed": 0, "started": time.time()}

    def run(tile):
        level, x, reverse_y, bbox = tile
        path = layer_dir / str(level) / str(x) / f"{reverse_y}.{extension}"
        if path.exists() and path.stat().st_size > 0:
            size = path.stat().st_size
        else:
            try:
                body = fetch(build_url(layer, date, image_format, bbox))
            except Exception as error:  # noqa: BLE001 - reported and counted below
                with lock:
                    state["failed"] += 1
                print(f"  failed z={level} x={x} y={reverse_y}: {error}", file=sys.stderr)
                return
            path.parent.mkdir(parents=True, exist_ok=True)
            temporary = path.with_suffix(path.suffix + ".part")
            temporary.write_bytes(body)
            os.replace(temporary, path)
            size = len(body)

        with lock:
            state["done"] += 1
            state["bytes"] += size
            done = state["done"]
            if done % 500 == 0 or done == total:
                elapsed = max(time.time() - state["started"], 1e-6)
                rate = done / elapsed
                remaining = (total - done) / rate if rate > 0 else 0
                print(
                    f"  {done:,}/{total:,} tiles  {state['bytes'] / 1e9:.2f} GB  "
                    f"{rate:.1f} tiles/s  ~{remaining / 60:.0f} min left",
                    flush=True)

    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as executor:
        for _ in executor.map(run, iterate_tiles(max_level)):
            pass

    complete = state["failed"] == 0
    manifest = {
        "layer": layer,
        "date": date,
        "format": "jpeg" if extension == "jpg" else "png",
        "extension": extension,
        "tile_size": TILE_SIZE,
        "root_tiles_x": 2,
        "root_tiles_y": 1,
        "max_level": max_level,
        "tile_count": total,
        "bytes": state["bytes"],
        "complete": complete,
        "source": WMS_ENDPOINT,
        "created_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
    }
    layer_dir.mkdir(parents=True, exist_ok=True)
    (layer_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    if not complete:
        print(f"  {state['failed']:,} tiles failed; rerun the same command to retry them.")
    return complete


def main():
    project_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--date", default=DEFAULT_DATE, help="observation date, YYYY-MM-DD")
    parser.add_argument("--layer", default=DEFAULT_LAYER, help="GIBS observation layer")
    parser.add_argument(
        "--format", choices=sorted(EXTENSIONS), default="png",
        help="png matches what the simulator streams (transparent no-data); "
             "jpeg is ~10x smaller but no-data areas turn black")
    parser.add_argument("--max-level", type=int, default=MAX_LEVEL, choices=range(MAX_LEVEL + 1))
    parser.add_argument("--out", type=Path, default=project_root / "ImageryPacks")
    parser.add_argument("--workers", type=int, default=8, help="parallel requests to GIBS")
    parser.add_argument("--skip-base", action="store_true", help="skip the Blue Marble base layer")
    parser.add_argument("--dry-run", action="store_true", help="print the size estimate and exit")
    args = parser.parse_args()

    try:
        datetime.strptime(args.date, "%Y-%m-%d")
    except ValueError:
        parser.error("--date must use YYYY-MM-DD")

    observation_gb = estimate_bytes(args.format, args.max_level) / 1e9
    base_gb = 0.0 if args.skip_base else estimate_bytes("base", args.max_level) / 1e9
    tiles = sum(tile_count(level) for level in range(args.max_level + 1))
    print(f"Levels 0-{args.max_level}: {tiles:,} tiles per layer")
    print(f"  {args.layer} {args.date} ({args.format}): ~{observation_gb:.1f} GB")
    if not args.skip_base:
        print(f"  {BASE_LAYER} (jpeg, shared by every date): ~{base_gb:.1f} GB")
    print(f"  Total: ~{observation_gb + base_gb:.1f} GB (estimate from sampled tiles)")
    if args.dry_run:
        return 0

    complete = download_layer(
        args.out, args.layer, args.date, args.format, args.max_level, args.workers)
    if not args.skip_base:
        complete &= download_layer(
            args.out, BASE_LAYER, STATIC_DATE, "jpeg", args.max_level, args.workers)
    return 0 if complete else 1


if __name__ == "__main__":
    sys.exit(main())
