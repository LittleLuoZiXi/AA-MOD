"""Read-only comparison of the frozen EnhanceHost with current bridge sources.

Nothing from the archived program is executed. Python code-object structure and
constants are compared, excluding filenames and line maps altered by freezing.
"""
import argparse
import hashlib
import json
import marshal
from pathlib import Path
import types

from PyInstaller.archive.readers import CArchiveReader


def canonical(value):
    if isinstance(value, types.CodeType):
        return {
            "kind": "code",
            "name": value.co_name,
            "qualname": value.co_qualname,
            "argcount": value.co_argcount,
            "posonlyargcount": value.co_posonlyargcount,
            "kwonlyargcount": value.co_kwonlyargcount,
            "nlocals": value.co_nlocals,
            "stacksize": value.co_stacksize,
            "flags": value.co_flags,
            "code": value.co_code.hex(),
            "exceptiontable": value.co_exceptiontable.hex(),
            "consts": [canonical(v) for v in value.co_consts],
            "names": value.co_names,
            "varnames": value.co_varnames,
            "freevars": value.co_freevars,
            "cellvars": value.co_cellvars,
        }
    if isinstance(value, (tuple, list)):
        return [canonical(v) for v in value]
    if isinstance(value, (set, frozenset)):
        return {"kind": type(value).__name__, "items": sorted(repr(v) for v in value)}
    if isinstance(value, bytes):
        return {"kind": "bytes", "value": value.hex()}
    if isinstance(value, (complex, type(Ellipsis))):
        return {"kind": type(value).__name__, "value": repr(value)}
    return value


def digest_code(code):
    data = json.dumps(canonical(code), ensure_ascii=False, sort_keys=True).encode("utf-8")
    return hashlib.sha256(data).hexdigest().upper()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    packaging = Path(__file__).resolve().parent
    mod = packaging.parent
    runtime = (args.runtime or packaging / "frozen" / "EnhanceHost").resolve()
    executable = runtime / "EnhanceHost.exe"
    archive = CArchiveReader(str(executable))
    pyz = archive.open_embedded_archive("PYZ.pyz")
    contract = json.loads((packaging / "enhancer-runtime-modules.json").read_text(encoding="utf-8"))
    required = set(contract["required"])
    checks = []
    intentionally_uncollected = []
    sources = [("enhance", mod / "bridge" / "enhance.py", "PYZ")]
    for path in sorted((mod / "bridge" / "vendor" / "dlss5tool").rglob("*.py")):
        rel = path.relative_to(mod / "bridge" / "vendor").with_suffix("")
        parts = list(rel.parts)
        if parts[-1] == "__init__":
            parts.pop()
        name = ".".join(parts)
        if name in required or name in pyz.toc:
            sources.append((name, path, "PYZ"))
        else:
            intentionally_uncollected.append(name)
    sources.append(("enhance_entry", packaging / "enhance_entry.py", "CArchive"))
    for name, source, location in sources:
        entry = {"module": name, "source": str(source), "source_sha256": sha(source)}
        if location == "PYZ" and name not in pyz.toc:
            entry.update(passed=False, reason="Module missing from frozen archive")
            checks.append(entry)
            continue
        frozen = pyz.extract(name) if location == "PYZ" else marshal.loads(archive.extract(name))
        frozen_hash = digest_code(frozen)
        matches = []
        for optimize in (0, 1, 2):
            compiled = compile(source.read_bytes(), str(source), "exec", optimize=optimize)
            if digest_code(compiled) == frozen_hash:
                matches.append(optimize)
        entry.update(passed=bool(matches), frozen_semantic_sha256=frozen_hash, matching_optimization_levels=matches)
        checks.append(entry)
    source_profiles = mod / "bridge" / "gpu_profiles.json"
    frozen_profiles = runtime / "_internal" / "gpu_profiles.json"
    checks.append({"data":"gpu_profiles.json", "passed":frozen_profiles.is_file() and sha(source_profiles)==sha(frozen_profiles), "source_sha256":sha(source_profiles), "frozen_sha256":sha(frozen_profiles) if frozen_profiles.is_file() else None})
    forbidden = sorted(n for n in pyz.toc if any(n == p or n.startswith(p + ".") for p in contract["excluded"]))
    checks.append({"data":"no_gui_or_model_modules", "passed":not forbidden, "unexpected":forbidden})
    result = {"passed": all(c["passed"] for c in checks), "method":"Read-only code-object comparison of the explicit headless runtime contract and every collected vendored module, excluding source filenames and line maps; no archived code executed, no GPU inference", "runtime":str(runtime), "executable_sha256":sha(executable), "required_modules":sorted(required), "uncollected_optional_modules":intentionally_uncollected, "checks":checks}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    failed = [c.get("module", c.get("data")) for c in checks if not c["passed"]]
    print(json.dumps({"passed":result["passed"],"checked":len(checks),"failed":failed,"report":str(args.output)}, ensure_ascii=False))
    raise SystemExit(0 if result["passed"] else 1)


if __name__ == "__main__":
    main()
