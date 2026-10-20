"""fetch_kernels.py: downloads use file:// URLs to synthetic files, never the network."""

import ast
import hashlib
import importlib.util
import json
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]

spec = importlib.util.spec_from_file_location("fetch_kernels", ROOT / "scripts" / "fetch_kernels.py")
fetch_kernels = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fetch_kernels)


@pytest.fixture
def kernel_set(tmp_path, monkeypatch):
    """A manifest whose files are served from tmp_path/source; the coverage check is skipped."""
    monkeypatch.setattr(fetch_kernels, "check_coverage", lambda manifest, kernels_dir: [])
    source = tmp_path / "source"
    source.mkdir()
    (tmp_path / "kernel_sets").mkdir()

    def entry(name, content):
        (source / name).write_bytes(content)
        return {
            "file": name,
            "urls": [(source / name).as_uri()],
            "sha256": hashlib.sha256(content).hexdigest(),
        }

    data = {
        "id": "test-set",
        "kernels": [entry("a.tls", b"leap seconds"), entry("b.bsp", b"ephemeris")],
        "support_files": [entry("WMM2025.COF", b"magnetic model")],
    }
    return tmp_path, source, data


def run(root, data):
    (root / "kernel_sets" / "test-set.json").write_text(json.dumps(data), encoding="utf-8")
    return fetch_kernels.main(["test-set", "--data-dir", str(root)])


def test_fetches_kernels_and_support_files_then_downloads_nothing(kernel_set, capsys):
    root, source, data = kernel_set
    assert run(root, data) == 0
    for name in ("a.tls", "b.bsp", "WMM2025.COF"):
        assert (root / "kernels" / name).read_bytes() == (source / name).read_bytes()
    assert capsys.readouterr().out.count("Downloading") == 3

    assert run(root, data) == 0
    assert "Downloading" not in capsys.readouterr().out


def test_falls_back_to_later_urls(kernel_set):
    root, source, data = kernel_set
    moved = source / "old_versions" / "b.bsp"
    moved.parent.mkdir()
    (source / "b.bsp").rename(moved)
    data["kernels"][1]["urls"].append(moved.as_uri())
    assert run(root, data) == 0
    assert (root / "kernels" / "b.bsp").read_bytes() == b"ephemeris"


def test_skips_a_url_whose_file_has_the_wrong_hash(kernel_set):
    root, source, data = kernel_set
    good = source / "good" / "a.tls"
    good.parent.mkdir()
    good.write_bytes(b"leap seconds")
    (source / "a.tls").write_bytes(b"newer leap seconds")
    data["kernels"][0]["urls"].append(good.as_uri())
    assert run(root, data) == 0
    assert (root / "kernels" / "a.tls").read_bytes() == b"leap seconds"


@pytest.mark.parametrize("section, index, name", [("kernels", 0, "a.tls"), ("support_files", 0, "WMM2025.COF")])
def test_wrong_download_is_never_left_in_kernels(kernel_set, capsys, section, index, name):
    root, source, data = kernel_set
    (source / name).write_bytes(b"tampered")
    assert run(root, data) == 1
    assert not (root / "kernels" / name).exists()
    assert not (root / "kernels" / f"{name}.part").exists()
    error = capsys.readouterr().err
    assert name in error
    assert data[section][index]["sha256"] in error
    assert hashlib.sha256(b"tampered").hexdigest() in error


def test_unreachable_urls_fail_naming_each_url(kernel_set, capsys):
    root, source, data = kernel_set
    (source / "b.bsp").unlink()
    assert run(root, data) == 1
    assert not (root / "kernels" / "b.bsp").exists()
    assert data["kernels"][1]["urls"][0] in capsys.readouterr().err


def test_altered_local_file_is_reported_and_kept(kernel_set, capsys):
    root, source, data = kernel_set
    (root / "kernels").mkdir()
    (root / "kernels" / "a.tls").write_bytes(b"edited")
    assert run(root, data) == 1
    assert (root / "kernels" / "a.tls").read_bytes() == b"edited"
    error = capsys.readouterr().err
    assert data["kernels"][0]["sha256"] in error
    assert hashlib.sha256(b"edited").hexdigest() in error


def test_unknown_set_names_manifest(tmp_path, capsys):
    assert fetch_kernels.main(["unknown", "--data-dir", str(tmp_path)]) == 1
    assert str(tmp_path / "kernel_sets" / "unknown.json") in capsys.readouterr().err


@pytest.mark.parametrize(
    "damage",
    [
        lambda data: data["support_files"][0].update(file="a.tls"),
        lambda data: data["kernels"][0].update(file="../a.tls"),
        lambda data: data["kernels"][0].update(urls=[]),
        lambda data: data["kernels"][0].update(sha256="abc"),
        lambda data: data.update(id="other-set"),
    ],
    ids=["duplicate", "path", "no-urls", "bad-hash", "wrong-id"],
)
def test_invalid_manifest_fetches_nothing(kernel_set, damage):
    root, source, data = kernel_set
    damage(data)
    assert run(root, data) == 1
    assert not (root / "kernels").exists()


# --- Coverage, against the real kernels when fetch_kernels.py has put them in kernels/ ----

REAL_SET = json.loads((ROOT / "kernel_sets" / "argus-2026-09-25.json").read_text())
has_real_kernels = all(
    (ROOT / "kernels" / entry["file"]).is_file() for entry in REAL_SET["kernels"]
)
real_kernels = pytest.mark.skipif(
    not has_real_kernels, reason="run scripts/fetch_kernels.py argus-2026-09-25 first"
)


@real_kernels
def test_real_set_covers_its_coverage_utc():
    pytest.importorskip("spiceypy")
    report = fetch_kernels.check_coverage(REAL_SET, ROOT / "kernels")
    assert all(line.endswith("OK") for line in report[1:])
    assert "2026-09-25T00:00:00.000Z" in report[-1]


@real_kernels
@pytest.mark.parametrize(
    "field, value, gap",
    [
        ("end", "2027-01-01T00:00:00Z", "ITRF93"),
        ("start", "1800-01-01T00:00:00Z", "Sun"),
        ("reconstructed_end", "2026-10-01T00:00:00Z", "reconstructed_end"),
    ],
)
def test_coverage_outside_the_files_fails(field, value, gap):
    pytest.importorskip("spiceypy")
    manifest = json.loads(json.dumps(REAL_SET))
    manifest["coverage_utc"][field] = value
    with pytest.raises(fetch_kernels.FetchError, match=gap):
        fetch_kernels.check_coverage(manifest, ROOT / "kernels")


def test_service_never_imports_the_tool_or_spiceypy():
    for path in (ROOT / "argus_basilisk").rglob("*.py"):
        for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
            if isinstance(node, ast.Import):
                names = [alias.name for alias in node.names]
            elif isinstance(node, ast.ImportFrom):
                names = [node.module or ""]
            else:
                continue
            for name in names:
                assert name.split(".")[0] not in ("fetch_kernels", "spiceypy"), path
