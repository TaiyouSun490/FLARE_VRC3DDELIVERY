"""Read-only checks for the portable core distribution archive."""
import hashlib
import json
import re
import sys
import tarfile
from pathlib import Path

package = Path(sys.argv[1])
with tarfile.open(package, "r:gz") as archive:
    entries = {entry.name: entry for entry in archive.getmembers()}
    paths = {}
    for name, entry in entries.items():
        if not name.endswith("/pathname"):
            continue
        guid = name.split("/")[0]
        assert re.fullmatch(r"[a-f0-9]{32}", guid), name
        path = archive.extractfile(entry).read().decode("utf-8").strip("\x00\r\n")
        assert path.startswith("Assets/"), path
        allowed = ("Assets/com.avatarcatalog.remote", "Assets/RemoteAvatarCatalogDistribution", "Assets/NightSlotMall", "Assets/SerializedUdonPrograms")
        assert any(path == base or path.startswith(base + "/") for base in allowed), path
        assert not path.lower().endswith((".rac2", ".fbx", ".unity")), path
        assert not any(part in path for part in ("FLAREGimmicks", "FlareGimmick", "PackagingVerification", "AvatarCatalogRoundTrip", "Assets/UdonSharp/")), path
        assert path not in paths, path
        meta = archive.extractfile(entries[guid + "/asset.meta"]).read().decode("utf-8")
        assert re.search(r"^guid: " + guid + r"\s*$", meta, re.M), path
        if "folderAsset: yes" not in meta:
            assert guid + "/asset" in entries, path
        paths[path] = guid
    for prefab in ("RAC2-ImagePad", "RAC2-ImagePad-Pedestal", "RAC2-Product-Pedestal"):
        path = "Assets/RemoteAvatarCatalogDistribution/Prefabs/" + prefab + ".prefab"
        content = archive.extractfile(entries[paths[path] + "/asset"]).read()
        assert b"night-slot-avatar-mall" not in content, path
    assert "Assets/com.avatarcatalog.remote/Runtime/AvatarCatalog.Remote.Runtime.UdonSharpAssembly.asset" in paths
    assert "Assets/RemoteAvatarCatalogDistribution/PORTABLE-0.2.4-JA.md" in paths
    for required in ("USER-GUIDE-JA.md", "USER-GUIDE-EN.md", "KNOWN-ISSUES.md"):
        assert "Assets/RemoteAvatarCatalogDistribution/" + required in paths, required
    assert "Assets/com.avatarcatalog.remote/Editor/FlareLocalization.cs" in paths
    manifest = json.loads(archive.extractfile(entries[paths["Assets/com.avatarcatalog.remote/package.json"] + "/asset"]).read())
    assert manifest["version"] == "0.2.5", manifest["version"]
    programs = [p for p in paths if p.startswith("Assets/SerializedUdonPrograms/") and p.endswith(".asset")]
    assert len(programs) == 6, programs
    print(f"PASS: {len(paths)} asset paths; valid GUID/meta layout; 3 prefabs; 6 owned Udon programs; assembly registration; no gimmick module/SDK test utilities/operator URL in prefabs")
print(f"bytes={package.stat().st_size}")
print(f"sha256={hashlib.sha256(package.read_bytes()).hexdigest()}")
