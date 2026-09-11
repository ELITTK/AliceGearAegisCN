"""Read-only Unity font inventory for Alice Gear Aegis cache files.

This deliberately does not rewrite bundles. It records enough information to
decide whether a runtime hybrid font can preserve the game's bitmap metrics.
"""
from pathlib import Path
import json, sys
import UnityPy

def inspect(path: Path):
    row = {"file": str(path), "fonts": [], "textures": []}
    try:
        env = UnityPy.load(str(path))
    except Exception as exc:
        row["error"] = f"{type(exc).__name__}: {exc}"
        return row
    for obj in env.objects:
        try:
            data = obj.read()
            typ = obj.type.name
            name = getattr(data, "m_Name", "") or ""
            if typ == "Font":
                chars = getattr(data, "m_CharacterRects", []) or []
                row["fonts"].append({
                    "name": name, "fontSize": getattr(data, "m_FontSize", None),
                    "lineHeight": getattr(data, "m_LineHeight", None),
                    "characterCount": len(chars),
                    "unicodeMin": min((getattr(c, "index", 0) for c in chars), default=0),
                    "unicodeMax": max((getattr(c, "index", 0) for c in chars), default=0),
                    "material": str(getattr(data, "m_DefaultMaterial", "")),
                })
            elif typ == "Texture2D" and ("font" in name.lower() or "common" in name.lower() or "deco" in name.lower()):
                row["textures"].append({"name": name, "width": getattr(data, "m_Width", None), "height": getattr(data, "m_Height", None), "format": str(getattr(data, "m_TextureFormat", ""))})
        except Exception as exc:
            row.setdefault("objectErrors", []).append(f"{obj.type.name}: {exc}")
    return row

def main(root):
    root = Path(root).expanduser()
    files = sorted(root.rglob("*.unity3d"))
    result = [inspect(p) for p in files]
    out = Path(__file__).with_name("font_inventory.json")
    out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    readable = [r for r in result if r.get("fonts")]
    print(f"scanned={len(result)} readable_with_fonts={len(readable)} output={out}")
    for r in readable:
        for f in r["fonts"]:
            print(f"{Path(r['file']).name}: {f['name']} chars={f['characterCount']} range=U+{f['unicodeMin']:04X}-U+{f['unicodeMax']:04X} size={f['fontSize']} line={f['lineHeight']}")

if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: analyze_fonts.py <cache-root>")
    main(sys.argv[1])
