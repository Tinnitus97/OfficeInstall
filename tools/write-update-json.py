#!/usr/bin/env python3
"""
Schreibt update.json fort - die Datei, die das Programm abfragt.

Bewusst nur EINE Datei fuer beide Staende: Das Programm holt sie mit einem
einzigen Abruf und weiss danach, ob es fuer das Programm ODER fuer die
Konfigurationen etwas Neues gibt.

Abgefragt wird sie ueber raw.githubusercontent.com und NICHT ueber die
GitHub-Schnittstelle (api.github.com): Die Schnittstelle laesst ohne Anmeldung
nur 60 Abrufe je Stunde und IP-Adresse zu. In einer Firma sitzen alle Rechner
hinter derselben Adresse - nach 60 Starts waere Schluss. raw.githubusercontent
liefert dagegen ueber ein Auslieferungsnetz aus und kennt diese Grenze nicht.

Aufruf (in der Regel aus dem Workflow):

  python tools/write-update-json.py --repo Tinnitus97/OfficeInstall \\
      --tag v1.0.1 --program-version 1.0.1 --program-sha256 abc...

  python tools/write-update-json.py --repo Tinnitus97/OfficeInstall \\
      --tag configs-70 --configs-version 70 --configs-date 16.08.2026 \\
      --configs-sha256 def...

Angegeben wird immer nur ein Teil; der andere bleibt unveraendert stehen.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

TARGET = Path("update.json")

LEER = {
    "schema": 1,
    "program": {"version": "", "released": "", "url": "", "sha256": "", "notes": ""},
    "configs": {"version": "", "date": "", "url": "", "sha256": "", "notes": ""},
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True, help="z.B. Tinnitus97/OfficeInstall")
    parser.add_argument("--tag", required=True, help="Etikett der Veroeffentlichung")

    parser.add_argument("--program-version")
    parser.add_argument("--program-sha256")

    parser.add_argument("--configs-version")
    parser.add_argument("--configs-date")
    parser.add_argument("--configs-sha256")

    args = parser.parse_args()

    data = json.loads(TARGET.read_text(encoding="utf-8")) if TARGET.exists() else dict(LEER)
    data.setdefault("schema", 1)
    data.setdefault("program", dict(LEER["program"]))
    data.setdefault("configs", dict(LEER["configs"]))

    base = f"https://github.com/{args.repo}/releases"
    from datetime import date

    if args.program_version:
        data["program"] = {
            "version": args.program_version,
            "released": date.today().isoformat(),
            "url": f"{base}/download/{args.tag}/OfficeInstall.exe",
            "sha256": (args.program_sha256 or "").lower(),
            "notes": f"{base}/tag/{args.tag}",
        }

    if args.configs_version:
        data["configs"] = {
            "version": args.configs_version,
            "date": args.configs_date or "",
            "url": f"{base}/download/{args.tag}/configs.zip",
            "sha256": (args.configs_sha256 or "").lower(),
            "notes": f"{base}/tag/{args.tag}",
        }

    TARGET.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(TARGET.read_text(encoding="utf-8"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
