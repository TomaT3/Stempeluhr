#!/usr/bin/env python3
"""Explicit PC/SC package migration, detached activation and timed rollback.

No package operations during --check or ordinary agent updates. --apply uses
the packages authenticated by the agent bundle's SHA-256. Original packages
must be downloadable before any service is stopped.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from contextlib import contextmanager

MIN_VERSION = "2.5.0"
PACKAGES = {"pcscd", "libpcsclite1", "libpcsclite-dev"}
FINISHED = {"ok", "rolled-back", "unchanged"}
AGENT_SERVICE = "stempeluhr-nfc-agent.service"
CONFIG = Path(os.environ.get("STEMPELUHR_AGENT_CONFIG", "/etc/stempeluhr-nfc-agent/config.json"))
BASE = Path(os.environ.get("STEMPELUHR_AGENT_DIR", "/opt/stempeluhr-nfc-agent"))
STATE = Path(os.environ.get("STEMPELUHR_PCSC_STATE", "/var/lib/stempeluhr-pcsc-migration"))


def command(*args, **kwargs):
    return subprocess.run(args, text=True, capture_output=True, check=True, timeout=120, **kwargs).stdout.strip()


def installed(package):
    try:
        status, version, architecture = command("dpkg-query", "-W", "-f=${db:Status-Status} ${Version} ${Architecture}", package).split()
        return (version, architecture) if status == "installed" else None
    except (subprocess.CalledProcessError, ValueError):
        return None


def at_least(version, minimum):
    return subprocess.run(["dpkg", "--compare-versions", version, "ge", minimum], check=False).returncode == 0


def suitable():
    versions = [installed(p) for p in ("pcscd", "libpcsclite1")]
    return all(v is not None and has_fix(v[0]) for v in versions)


def has_fix(version):
    # A Debian epoch changes package ordering, not the upstream fix level.
    return at_least(version.split(":", 1)[-1], MIN_VERSION)


def distribution():
    values = dict(line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines() if "=" in line)
    return values.get("VERSION_CODENAME", "").strip('"') if values.get("ID", "").strip('"') == "debian" else None


@contextmanager
def locked():
    BASE.mkdir(parents=True, exist_ok=True)
    with (BASE / ".maintenance.lock").open("a") as handle:
        fcntl.flock(handle, fcntl.LOCK_EX)
        yield


def package_info(path):
    return command("dpkg-deb", "-f", str(path), "Package", "Version", "Architecture").splitlines()


def packages_from_manifest(directory, architecture):
    manifest = json.loads((directory / "manifest.json").read_text())
    if manifest["distribution"] != "trixie" or manifest["architecture"] != architecture:
        raise ValueError("PC/SC-Bundle passt nicht zu Distribution/Architektur")
    version = manifest["version"]
    if not has_fix(version):
        raise ValueError("PC/SC-Bundle enthält den erforderlichen Fixstand nicht")
    result = {}
    for entry in manifest["packages"]:
        filename, digest = entry["file"], entry["sha256"]
        if not re.fullmatch(r"[A-Za-z0-9.+~_-]+\.deb", filename) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ValueError("Ungültiger Paketname oder SHA-256")
        path = directory / filename
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError("PC/SC-Paketprüfsumme stimmt nicht")
        # dpkg-deb with multiple fields prefixes each line with its field name.
        info = package_info(path)
        fields = dict(line.split(": ", 1) for line in info)
        package = fields["Package"]
        if package not in PACKAGES or package in result or fields["Version"] != version or fields["Architecture"] != architecture:
            raise ValueError("PC/SC-Paketmetadaten passen nicht zum Manifest")
        result[package] = path
    if not {"pcscd", "libpcsclite1"} <= result.keys():
        raise ValueError("PC/SC-Bundle unvollständig")
    return version, result


def simulate(paths):
    output = command("apt-get", "-s", "--no-install-recommends", "--allow-downgrades", "install", *(str(p) for p in paths))
    for line in output.splitlines():
        if line.startswith("Remv ") or (line.startswith("Inst ") and line.split()[1].split(":")[0] not in PACKAGES):
            raise ValueError("Paketwechsel würde weitere Systempakete ändern; manuell prüfen")


def active(service):
    return subprocess.run(["systemctl", "is-active", "--quiet", service], check=False).returncode == 0


def write_result(folder, status, detail):
    temporary = folder / "result.tmp"
    temporary.write_text(json.dumps({"status": status, "detail": detail}) + "\n")
    temporary.replace(folder / "result.json")


def read_result(folder):
    try:
        return json.loads((folder / "result.json").read_text())
    except FileNotFoundError:
        return None


def prepare():
    with locked():
        pending = STATE / "pending.json"
        if pending.exists():
            previous = Path(json.loads(pending.read_text())["folder"])
            result = read_result(previous)
            if result is None or result["status"] not in FINISHED:
                raise ValueError("Eine Paketmigration ist noch offen; zuerst deren Dienstjournal/Originalsicherung prüfen")
            pending.unlink()
        if suitable():
            print("PC/SC-Fixstand bereits geeignet; kein Paketwechsel.")
            return None
        if distribution() != "trixie":
            raise ValueError("Paketmigration unterstützt derzeit nur Trixie; keine Pakete geändert")
        architecture = command("dpkg", "--print-architecture")
        if architecture != "arm64":
            raise ValueError("Paketmigration unterstützt derzeit nur arm64; keine Pakete geändert")
        version, packages = packages_from_manifest(Path(__file__).resolve().parent / "pcsc", architecture)
        old = {package: installed(package) for package in PACKAGES}
        selected = {p: path for p, path in packages.items() if old[p] is not None or p != "libpcsclite-dev"}
        if old["libpcsclite-dev"] is not None and "libpcsclite-dev" not in selected:
            raise ValueError("Installiertes Entwicklungspaket fehlt im Bundle")
        if any(old[p] and at_least(old[p][0], version) and old[p][0] != version for p in selected):
            raise ValueError("Keine neuere installierte PC/SC-Version durch Backport ersetzen")
        held = set(command("apt-mark", "showhold").splitlines())
        if held & selected.keys():
            raise ValueError("PC/SC-Pakete sind gehalten; Hold zuerst ausdrücklich klären")
        simulate(selected.values())
        STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
        folder = STATE / f"{time.time_ns()}"
        folder.mkdir(mode=0o700)
        (folder / "old").mkdir()
        (folder / "new").mkdir()
        for p, path in selected.items():
            shutil.copy2(path, folder / "new" / path.name)
            if old[p] is not None:
                command("apt-get", "download", f"{p}={old[p][0]}", cwd=folder / "old")
        # Verify that the downloaded rollback packages really match installed versions.
        originals = list((folder / "old").glob("*.deb"))
        verified = {}
        for path in originals:
            fields = dict(line.split(": ", 1) for line in package_info(path))
            p = fields["Package"]
            if p not in selected or old[p] != (fields["Version"], fields["Architecture"]):
                raise ValueError("Rückinstallationspaket passt nicht zum installierten Original")
            verified[p] = path
        if set(verified) != {p for p in selected if old[p] is not None}:
            raise ValueError("Originalpakete für Rückinstallation fehlen")
        simulate(originals)
        (folder / "pcsc-config").mkdir()
        conffiles = []
        for p in selected:
            if old[p] is None:
                continue
            for line in command("dpkg-query", "-W", "-f=${Conffiles}", p).splitlines():
                path = Path(line.split()[0]) if line.split() else None
                if path and path.is_file() and str(path).startswith("/etc/") and ".." not in path.parts:
                    relative = path.relative_to("/etc")
                    target = folder / "pcsc-config" / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(path, target)
                    conffiles.append(str(relative))
        if CONFIG.exists():
            shutil.copy2(CONFIG, folder / "config.json")
        config = json.loads(CONFIG.read_text()) if CONFIG.exists() else {}
        snapshot = {"agentActive": active(AGENT_SERVICE), "pcscActive": active("pcscd.service"),
                    "port": config.get("local_port", 8737), "readerFilter": config.get("reader_name_contains"),
                    "auto": sorted(set(command("apt-mark", "showauto").splitlines()) & selected.keys()),
                    "rollbackUnit": "stempeluhr-pcsc-rollback-" + folder.name,
                    "originals": {p: list(old[p]) for p in selected if old[p] is not None},
                    "conffiles": conffiles, "targetVersion": version}
        (folder / "snapshot.json").write_text(json.dumps(snapshot))
        shutil.copy2(__file__, folder / "pcsc_maintenance.py")
        shutil.copy2(Path(__file__).with_name("probe_reader.py"), folder / "probe_reader.py")
        invocation = [sys.executable, str(folder / "pcsc_maintenance.py")]
        environment = ["--setenv=STEMPELUHR_AGENT_DIR=" + str(BASE), "--setenv=STEMPELUHR_PCSC_STATE=" + str(STATE)]
        # Timer is armed before activation; both jobs continue after SSH disconnects.
        command("systemd-run", "--unit=" + snapshot["rollbackUnit"], "--on-active=5m", "--collect",
                *environment, *invocation, "--rollback", str(folder))
        pending.write_text(json.dumps({"folder": str(folder)}))
        try:
            command("systemd-run", "--unit=stempeluhr-pcsc-activate-" + folder.name, "--collect",
                    "--property=RuntimeMaxSec=4min",
                    *environment, *invocation, "--activate", str(folder))
        except Exception:
            # No activation started: the armed rollback remains a harmless safety net.
            raise
        print("Paketwechsel gestartet; Protokoll und Originalsicherung:", folder)
        return folder


def http_json(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=3) as response:
        return json.load(response)


def verify(folder, snapshot, require_fix=True):
    if require_fix and not suitable():
        raise ValueError("Installierter PC/SC-Fixstand ungeeignet")
    # runuser exercises the actual Polkit permissions of the service account.
    # The snapshot directory is root-only. Pass public probe code as an argument
    # so the service user never needs access to backed-up credentials/packages.
    command("runuser", "-u", "stempeluhr", "--", sys.executable, "-c", (folder / "probe_reader.py").read_text(),
            snapshot["readerFilter"] or "")
    if not active("pcscd.service"):
        raise ValueError("pcscd ist nach der Leserprüfung nicht aktiv")
    if snapshot["agentActive"]:
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            try:
                health = http_json(snapshot["port"], "/health")
                diagnostics = http_json(snapshot["port"], "/diagnostics")
                if active(AGENT_SERVICE) and health.get("ok") and diagnostics.get("uiStatus") == "alive":
                    print("Agent, PC/SC-Leser und Browser-Lebenszeichen geprüft. Echter Karten-Scan noch ausstehend.")
                    return
            except (OSError, ValueError):
                pass
            time.sleep(2)
        raise ValueError("Agent oder Browser-Lebenszeichen fehlt")
    print("PC/SC-Leser geprüft. Agent-/Browserprüfung und echter Scan nach Einrichtung erforderlich.")


def restore_auto(snapshot):
    if snapshot["auto"]:
        command("apt-mark", "auto", *snapshot["auto"])


def rollback(folder, snapshot):
    # A separately started APT update must not be overwritten by our rollback.
    for p, expected in snapshot["originals"].items():
        current = installed(p)
        if current is not None and current[0] not in (expected[0], snapshot.get("targetVersion")):
            if snapshot["agentActive"]:
                command("systemctl", "start", AGENT_SERVICE)
            raise ValueError("PC/SC-Version wurde außerhalb der Migration verändert; kein automatisches Downgrade")
    command("systemctl", "stop", AGENT_SERVICE)
    originals = list((folder / "old").glob("*.deb"))
    if originals:
        command("apt-get", "-y", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold",
                "--no-install-recommends", "--allow-downgrades", "install", *(str(p) for p in originals))
    for name in snapshot["conffiles"]:
        relative = Path(name)
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError("Ungültiger Konfigurationspfad in Originalsicherung")
        shutil.copy2(folder / "pcsc-config" / relative, Path("/etc") / relative)
    restore_auto(snapshot)
    for p, expected in snapshot["originals"].items():
        if installed(p) != tuple(expected):
            raise ValueError("Originalversion nach Rückinstallation nicht wiederhergestellt")
    command("systemctl", "restart", "pcscd.service")
    if snapshot["agentActive"]:
        command("systemctl", "start", AGENT_SERVICE)
    verify(folder, snapshot, require_fix=False)
    if not snapshot["pcscActive"] and not snapshot["agentActive"]:
        command("systemctl", "stop", "pcscd.service")
    write_result(folder, "rolled-back", "Originalpakete wiederhergestellt; Funktionsprüfung vor Ort weiterhin erforderlich")


def activate(folder):
    with locked():
        snapshot = json.loads((folder / "snapshot.json").read_text())
        if any(installed(p) != tuple(expected) for p, expected in snapshot["originals"].items()):
            write_result(folder, "unchanged", "PC/SC-Pakete seit Vorprüfung verändert; Paketwechsel ohne Dienstunterbrechung abgebrochen")
            subprocess.run(["systemctl", "stop", snapshot["rollbackUnit"] + ".timer"], check=False)
            return
        try:
            command("systemctl", "stop", AGENT_SERVICE)
            command("apt-get", "-y", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold",
                    "--no-install-recommends", "install", *(str(p) for p in (folder / "new").glob("*.deb")))
            restore_auto(snapshot)
            command("systemctl", "restart", "pcscd.service")
            if snapshot["agentActive"]:
                command("systemctl", "start", AGENT_SERVICE)
            verify(folder, snapshot)
            write_result(folder, "ok", "Fixstand und Leser geprüft; echter Scan und 24h-Vergleich noch ausstehend")
            # Once committed, even an already dispatched timer skips rollback.
            subprocess.run(["systemctl", "stop", snapshot["rollbackUnit"] + ".timer"], check=False)
        except Exception as error:
            print("Aktivierung fehlgeschlagen; Rückinstallation:", type(error).__name__, file=sys.stderr)
            try:
                rollback(folder, snapshot)
            except Exception:
                write_result(folder, "rollback-failed", "Rückinstallation fehlgeschlagen; Timer versucht erneut; Journal prüfen")
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_mutually_exclusive_group(required=True)
    actions.add_argument("--check", action="store_true")
    actions.add_argument("--apply", action="store_true")
    actions.add_argument("--activate", type=Path, help=argparse.SUPPRESS)
    actions.add_argument("--rollback", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.check:
        ok = suitable()
        print("PC/SC-Fixstand geeignet (>= 2.5.0)." if ok else "PC/SC-Fixstand fehlt; explizite Paketmigration erforderlich (siehe Pi-Anleitung).")
        return 0 if ok else 1
    if os.geteuid() != 0:
        raise ValueError("Paketmigration benötigt root")
    if args.activate:
        activate(args.activate.resolve())
    elif args.rollback:
        with locked():
            result = read_result(args.rollback)
            if result and result["status"] in FINISHED:
                return 0
            rollback(args.rollback, json.loads((args.rollback / "snapshot.json").read_text()))
    else:
        folder = prepare()
        if folder:
            deadline = time.monotonic() + 240
            while time.monotonic() < deadline:
                result = read_result(folder)
                if result:
                    print(result["detail"])
                    return 0 if result["status"] == "ok" else 1
                time.sleep(2)
            raise ValueError("Paketwechsel dauert länger; Dienstjournal prüfen; Rollback-Timer bleibt aktiv")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("PC/SC-Migration fehlgeschlagen:", error, file=sys.stderr)
        sys.exit(1)
