"""Register a published ShelfCheck ZIP in the Jellyfin catalog."""
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

version, archive = sys.argv[1:]
catalog = Path("manifest.json")
packages = json.loads(catalog.read_text(encoding="utf-8"))
if not packages:
    packages.append({
        "guid": "787553a2-52ba-476a-8393-8c5d2fe89ce5",
        "name": "ShelfCheck",
        "description": "Prüft lokale Videos, Poster, Staffelordner sowie Trailer und gleicht das Hinzufügedatum mit der Veröffentlichung ab.",
        "overview": "Bestandsprüfung und Datumsabgleich für Film- und Serienbibliotheken.",
        "owner": "brudertomate",
        "category": "General",
        "versions": []
    })

project = ET.parse("ShelfCheck/ShelfCheck.csproj")
abi = project.find(".//PackageReference[@Include='Jellyfin.Controller']").attrib["Version"] + ".0"
package = packages[0]
entries = [entry for entry in package["versions"] if entry["version"] != version]
entries.append({
    "version": version,
    "targetAbi": abi,
    "sourceUrl": f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/releases/download/v{version}/{Path(archive).name}",
    "checksum": hashlib.md5(Path(archive).read_bytes()).hexdigest(),
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "changelog": f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/releases/tag/v{version}",
})
package["versions"] = sorted(entries, key=lambda entry: tuple(map(int, entry["version"].split("."))), reverse=True)
catalog.write_text(json.dumps(packages, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
