"""Fetch and verify the files of a kernel set into kernels/.

PLACEHOLDER: not implemented.

TODO(spice-team): port ensure_kernels/download from
origin/spice-integration:Argus.Spice/generate_reference.py. Download each manifest file from its
URLs (with the a_old_versions fallbacks), verify its SHA-256, and refuse a mismatch. Run it
before starting the service; the service itself never downloads (README §8). Also fetch and pin
WMM2025.COF.
"""
