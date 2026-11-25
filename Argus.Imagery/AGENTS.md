# AGENTS.md: Argus.Imagery

An optional tool that downloads NASA GIBS imagery once, so Unity can read the base and observation
layers from disk instead of streaming them. Interactive use doesn't need it. Usage, sizes and the
pack layout are in [README.md](README.md).

```bash
python3 Argus.Imagery/download_gibs_pack.py --dry-run          # size estimate only; downloads nothing
python3 Argus.Imagery/download_gibs_pack.py --date 2025-06-21  # default layer and date
```

## Rules

- **Python 3 standard library only.** The script must run with a bare `python3`; don't add
  dependencies or a venv. `Argus.Basilisk/.venv` is the repository's only Python environment.
- **Never start a real download to test a change.** A full pack is about 31 GB (about 5 GB as JPEG)
  and takes hours. Use `--dry-run`, or a small `--max-level` with `--out` pointing at a temporary
  directory.
- **Never commit a pack.** Packs go to `ImageryPacks/` (git-ignored, not imported by Unity) or to
  `--out`.
- **The pack format is read by Unity.** The tile grid (512 px geographic tiles, levels 0–8), file
  layout and `manifest.json` must match what Unity's loader (`Unity/Cesium/NasaGibsRasterController.cs`) expects; change both sides
  together and update the README.
- `manifest.json` is marked complete only after every tile succeeds, and reruns skip finished
  tiles. Keep both properties: Unity rejects incomplete packs.
- Night lights and Cesium World Terrain are always streamed; the pack doesn't cover them.
