# Offline NASA GIBS imagery pack

By default the simulator streams NASA GIBS imagery tile by tile while it runs. For tests and
dataset generation you can instead download the full-resolution imagery once and have Unity
read it from disk. Runs then give identical pixels every time, work without GIBS, and never
hit a load timeout.

This is optional. Interactive use does not need it.

## Size

The simulator requests 512 px geographic tiles, levels 0–8. Level 8 is about 150 m per pixel,
the finest the overlay asks for. That is 174,762 tiles per layer. The sizes below are estimates
from tiles sampled from GIBS in September 2026:

| Layer | Format | Size |
|---|---|---|
| VIIRS true color, one date | PNG (what the simulator streams) | ~30 GB |
| VIIRS true color, one date | JPEG (`--format jpeg`) | ~3 GB |
| Blue Marble base (shared by every date) | JPEG | ~1.6 GB |

- **Total size:** about 31 GB for the default PNG pack, or about 5 GB with `--format jpeg`.
- **Trade-off with JPEG:** areas with no data are transparent in PNG, so the Blue Marble base
  shows through. In JPEG they are black, so polar night and orbit gaps turn black.
- **Each extra date** adds another observation layer, but the base layer is reused.
- **Download time:** at the default 8 parallel requests, expect roughly 5–10 hours.

## Download

Python 3 standard library only; nothing to install.

```bash
python3 Argus.Imagery/download_gibs_pack.py --dry-run          # print the estimate only
python3 Argus.Imagery/download_gibs_pack.py --date 2025-06-21  # default layer and date
```

- **Location:** the pack goes to `ImageryPacks/` in the project root. Git ignores it and
  Unity doesn't import it. Use `--out` to put it elsewhere, for example on an external drive.
- **Interruptions:** if the download stops, rerun the same command and it skips finished
  tiles. `manifest.json` is marked complete only when every tile has succeeded.
- **Other options:** `--layer`, `--format`, `--max-level`, `--workers`, `--skip-base`.

## Use it in Unity

Pick one:

- **In the Editor:** select **Cesium World Terrain + NASA GIBS** and turn on **Use Imagery
  Pack**. Set **Imagery Pack Directory** if the pack isn't in `ImageryPacks/`.
- **From the command line:** add `-gt-pack <directory>`.

Unity uses the pack for the base layer and for the observation layer whose date matches the
current setting. If that part of the pack is missing, incomplete, or built with a lower
`--max-level` than the overlay uses, Unity logs a warning and streams that layer instead.

**Still streamed:**

- **Night lights:** its tiles follow the moving night-side rectangle, so they never line up
  with a downloaded grid. They are small (about 3 KB per tile).
- **Cesium World Terrain:** it comes from Cesium ion, which this tool does not mirror.
