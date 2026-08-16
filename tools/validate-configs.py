#!/usr/bin/env python3
"""
Prueft die Konfigurationsdateien des Office-Pakets.

Aufruf:  python tools/validate-configs.py package

Geprueft wird genau das, was beim Ausrollen schiefgehen kann und in der XML
nicht auffaellt:

  1. Ist die Datei ueberhaupt wohlgeformt und hat sie <Add><Product>?
  2. Passt Channel zum Ordner?  -> sonst bricht das Deployment Tool ab mit
     "This product can't be installed on the selected update channel"
  3. Passt OfficeClientEdition zum Ordner x32/x64?
  4. Ist die Produktkennung bekannt und passt sie zur Installationsbasis?
     (Volumenlizenz gehoert nicht nach Current, Retail nicht nach PerpetualVL)
  5. Kommt eine Produktkennung innerhalb einer Basis doppelt vor?
  6. Ist eine Sprache angegeben, und ueberall dieselbe?

Rueckgabewert 0 = alles in Ordnung, 1 = mindestens ein Fehler.
Warnungen allein aendern den Rueckgabewert nicht.
"""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

# Die vier Installationsbasen. "Current" ist der einzige Ordner, in dem das
# Attribut Channel fehlen darf - dort ist Current ohnehin die Vorgabe.
BASES = ("Current", "PerpetualVL2019", "PerpetualVL2021", "PerpetualVL2024")
ARCHITECTURES = {"x32": "32", "x64": "64"}

# Produktkennungen, die in einer Volumenlizenz-Basis stehen duerfen.
VOLUME_SUFFIX = "Volume"

errors: list[str] = []
warnings: list[str] = []


def error(path: Path, message: str) -> None:
    errors.append(f"{path}: {message}")


def warn(path: Path, message: str) -> None:
    warnings.append(f"{path}: {message}")


def check_file(path: Path, arch: str, base: str, seen: dict) -> None:
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as ex:
        error(path, f"XML nicht lesbar - {ex}")
        return

    if root.tag != "Configuration":
        error(path, f"Wurzelelement ist <{root.tag}>, erwartet <Configuration>")
        return

    add = root.find("Add")
    if add is None:
        error(path, "kein <Add>-Element")
        return

    # ---- Kanal ------------------------------------------------------------
    channel = add.get("Channel")
    if base == "Current":
        if channel not in (None, "Current"):
            error(path, f'Channel="{channel}" - im Ordner Current ist nur "Current" zulaessig')
        elif channel is None:
            warn(path, "kein Channel angegeben - der Ordner Current wird angenommen")
    else:
        if channel is None:
            warn(path, f"kein Channel angegeben - erwartet wird {base}")
        elif channel != base:
            error(path, f'Channel="{channel}" passt nicht zum Ordner {base}')

    # ---- Architektur ------------------------------------------------------
    edition = add.get("OfficeClientEdition")
    expected = ARCHITECTURES[arch]
    if edition is None:
        error(path, "OfficeClientEdition fehlt")
    elif edition != expected:
        error(path, f'OfficeClientEdition="{edition}" passt nicht zum Ordner {arch}')

    # ---- Produkte ---------------------------------------------------------
    products = add.findall("Product")
    if not products:
        error(path, "kein <Product>-Element")
        return
    if len(products) > 1:
        warn(path, f"{len(products)} Produkte in einer Datei - das Programm zeigt nur das erste an")

    for product in products:
        product_id = product.get("ID")
        if not product_id:
            error(path, "<Product> ohne ID")
            continue

        is_volume = product_id.endswith(VOLUME_SUFFIX)
        if base == "Current" and is_volume:
            error(path, f"{product_id} ist eine Volumenlizenz und gehoert nicht nach Current")
        if base != "Current" and not is_volume:
            error(path, f"{product_id} ist keine Volumenlizenz und gehoert nicht nach {base}")

        # Jahreszahl im Produkt gegen die Basis pruefen (2019/2021/2024)
        if base.startswith("PerpetualVL"):
            year = base[len("PerpetualVL"):]
            if year not in product_id:
                warn(path, f"{product_id} enthaelt nicht die Jahreszahl {year} der Basis")

        key = (arch, base, product_id)
        if key in seen:
            error(path, f"{product_id} steht auch in {seen[key].name}")
        else:
            seen[key] = path

        languages = product.findall("Language")
        if not languages:
            error(path, f"{product_id} ohne <Language>")
        for language in languages:
            if not language.get("ID"):
                error(path, f"{product_id}: <Language> ohne ID")


def main(argv: list[str]) -> int:
    root = Path(argv[1] if len(argv) > 1 else "package").resolve()

    if not root.is_dir():
        print(f"Ordner nicht gefunden: {root}")
        return 1

    seen: dict = {}
    count = 0
    languages: set[str] = set()

    for arch in ARCHITECTURES:
        arch_folder = root / arch
        if not arch_folder.is_dir():
            warnings.append(f"{arch_folder}: Ordner fehlt")
            continue

        for base_folder in sorted(p for p in arch_folder.iterdir() if p.is_dir()):
            base = base_folder.name
            if base not in BASES:
                warnings.append(f"{base_folder}: unbekannte Installationsbasis - wird uebergangen")
                continue

            files = sorted(base_folder.glob("*.xml"))
            if not files:
                warnings.append(f"{base_folder}: keine XML-Datei")

            for file in files:
                count += 1
                check_file(file, arch, base, seen)
                try:
                    for language in ET.parse(file).getroot().iter("Language"):
                        if language.get("ID"):
                            languages.add(language.get("ID"))
                except ET.ParseError:
                    pass

    # Beide Architekturen sollten dieselben Produkte anbieten.
    per_arch = defaultdict(set)
    for arch, base, product_id in seen:
        per_arch[arch].add((base, product_id))
    if len(per_arch) == 2:
        x32, x64 = per_arch["x32"], per_arch["x64"]
        for base, product_id in sorted(x64 - x32):
            warnings.append(f"x32/{base}: {product_id} fehlt (in x64 vorhanden)")
        for base, product_id in sorted(x32 - x64):
            warnings.append(f"x64/{base}: {product_id} fehlt (in x32 vorhanden)")

    if len(languages) > 1:
        warnings.append(f"mehrere Sprachen im Paket: {', '.join(sorted(languages))}")

    print(f"{count} Konfigurationsdatei(en) geprueft in {root}")
    print()

    for message in warnings:
        print(f"  [!] {message}")
    for message in errors:
        print(f"  [FEHLER] {message}")

    print()
    if errors:
        print(f"{len(errors)} Fehler, {len(warnings)} Warnung(en) - nicht in Ordnung.")
        return 1

    print(f"Keine Fehler, {len(warnings)} Warnung(en).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
