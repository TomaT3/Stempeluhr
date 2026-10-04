#!/usr/bin/env python3
"""PC/SC package migration, detached activation and timed rollback.

--check makes no changes. --apply uses the packages authenticated by the agent
bundle's SHA-256; original packages must be downloadable before any service is
stopped. The agent updater starts --apply --auto once per bundled package
version; a failed automatic attempt is not repeated without --apply. A
migration interrupted by a reboot is rolled back by the next --apply.
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
# "failed" is followed by a rollback; these end a migration run.
TERMINAL = FINISHED | {"rollback-failed"}
ROLLBACK_DELAY = "20m"
ACTIVATION_LIMIT = "15min"
RESULT_WAIT_SECONDS = 25 * 60
# unattended-upgrades may hold the dpkg lock for several minutes.
DPKG_LOCK_WAIT_SECONDS = 10 * 60
AGENT_SERVICE = "stempeluhr-nfc-agent.service"
CONFIG = Path(os.environ.get("STEMPELUHR_AGENT_CONFIG", "/etc/stempeluhr-nfc-agent/config.json"))
BASE = Path(os.environ.get("STEMPELUHR_AGENT_DIR", "/opt/stempeluhr-nfc-agent"))
STATE = Path(os.environ.get("STEMPELUHR_PCSC_STATE", "/var/lib/stempeluhr-pcsc-migration"))
OS_RELEASE = Path(os.environ.get("STEMPELUHR_OS_RELEASE", "/etc/os-release"))
BOOT_ID = Path("/proc/sys/kernel/random/boot_id")
APT_INSTALL = ("apt-get", "-y", "-o", f"DPkg::Lock::Timeout={DPKG_LOCK_WAIT_SECONDS}",
               "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold",
               "--no-install-recommends")


def command(*args, timeout=120, **kwargs):
    return subprocess.run(args, text=True, capture_output=True, check=True, timeout=timeout, **kwargs).stdout.strip()


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
    values = dict(line.split("=", 1) for line in OS_RELEASE.read_text().splitlines() if "=" in line)
    return values.get("VERSION_CODENAME", "").strip('"') if values.get("ID", "").strip('"') == "debian" else None


def platform_supported():
    """Only Trixie/arm64 bundles carry packages; other systems keep their pcscd."""
    try:
        return (bundle_version() is not None and distribution() == "trixie"
                and command("dpkg", "--print-architecture") == "arm64")
    except (OSError, subprocess.CalledProcessError):
        return False


@contextmanager
def locked():
    BASE.mkdir(parents=True, exist_ok=True)
    with (BASE / ".maintenance.lock").open("a") as handle:
        fcntl.flock(handle, fcntl.LOCK_EX)
        yield


def bundle_version():
    try:
        return json.loads((Path(__file__).resolve().parent / "pcsc" / "manifest.json").read_text())["version"]
    except (OSError, ValueError, KeyError):
        return None


def auto_marker():
    return STATE / "auto-attempt.json"


def auto_possible():
    """An automatic attempt is made once per bundled package version."""
    version = bundle_version()
    if version is None:
        return False
    try:
        return json.loads(auto_marker().read_text()).get("targetVersion") != version
    except (OSError, ValueError):
        return True


def current_boot():
    try:
        return BOOT_ID.read_text().strip()
    except OSError:
        return None


def pending_migration():
    """(folder, boot id) of a migration without a final result, else None."""
    try:
        pending = json.loads((STATE / "pending.json").read_text())
        folder = Path(pending["folder"])
    except (OSError, ValueError, KeyError):
        return None
    result = read_result(folder)
    if result is not None and result["status"] in TERMINAL:
        return None
    return folder, pending.get("bootId")


def interrupted_migration():
    """Folder of an unfinished migration whose units died with a reboot.

    Activation and rollback timer are transient systemd units, which do not
    survive a reboot; nothing would ever finish or roll back such a run.
    """
    pending = pending_migration()
    if pending is None:
        return None
    folder, boot = pending
    return folder if boot is not None and boot != current_boot() else None


def migration_running():
    return pending_migration() is not None and interrupted_migration() is None


def record_auto_attempt(version):
    # Also written for manual runs: after a failed or manually rolled back
    # change the updater must not start the same package version again.
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    auto_marker().write_text(json.dumps({"targetVersion": version, "startedAt": time.time()}) + "\n")


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
        # No separate rollback simulation: before the change it would only
        # reinstall the same versions. The forward simulation already rejects
        # any change to other packages, so the rollback is its exact inverse.
        record_auto_attempt(version)
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
        agent_active = active(AGENT_SERVICE)
        port = config.get("local_port", 8737)
        snapshot = {"agentActive": agent_active, "pcscActive": active("pcscd.service"),
                    # A fresh install has no kiosk yet: only a browser seen
                    # before the change is required afterwards.
                    "browserAlive": agent_active and browser_alive(port),
                    "port": port, "readerFilter": config.get("reader_name_contains"),
                    "auto": sorted(set(command("apt-mark", "showauto").splitlines()) & selected.keys()),
                    "rollbackUnit": "stempeluhr-pcsc-rollback-" + folder.name,
                    "originals": {p: list(old[p]) for p in selected if old[p] is not None},
                    "conffiles": conffiles, "targetVersion": version}
        (folder / "snapshot.json").write_text(json.dumps(snapshot))
        shutil.copy2(__file__, folder / "pcsc_maintenance.py")
        shutil.copy2(Path(__file__).with_name("probe_reader.py"), folder / "probe_reader.py")
        # Timer is armed before activation; both jobs continue after SSH disconnects.
        # Without a started activation it remains a harmless safety net.
        detached(folder, snapshot["rollbackUnit"], "--rollback", "--on-active=" + ROLLBACK_DELAY)
        pending.write_text(json.dumps({"folder": str(folder), "bootId": current_boot()}))
        detached(folder, "stempeluhr-pcsc-activate-" + folder.name, "--activate",
                 "--property=RuntimeMaxSec=" + ACTIVATION_LIMIT)
        print("Paketwechsel gestartet; Protokoll und Originalsicherung:", folder)
        return folder


def detached(folder, unit, action, *options):
    """Runs the archived copy of this script as its own transient unit."""
    command("systemd-run", "--unit=" + unit, "--collect", *options,
            "--setenv=STEMPELUHR_AGENT_DIR=" + str(BASE), "--setenv=STEMPELUHR_PCSC_STATE=" + str(STATE),
            sys.executable, str(folder / "pcsc_maintenance.py"), action, str(folder))


def http_json(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=3) as response:
        return json.load(response)


def browser_alive(port, wait_seconds=30):
    """Whether the kiosk page sends heartbeats; waits out a fresh agent restart."""
    deadline = time.monotonic() + wait_seconds
    while True:
        try:
            status = http_json(port, "/diagnostics").get("uiStatus")
        except (OSError, ValueError):
            status = None
        # "not-seen": the agent restarted and the next heartbeat is due within 15 s.
        if status != "not-seen" or time.monotonic() >= deadline:
            return status == "alive"
        time.sleep(2)


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
                browser_ok = not snapshot.get("browserAlive") or diagnostics.get("uiStatus") == "alive"
                if active(AGENT_SERVICE) and health.get("ok") and browser_ok:
                    checked = "Browser-Lebenszeichen" if snapshot.get("browserAlive") else "Agent-Health (vorher kein Browser)"
                    print(f"Agent, PC/SC-Leser und {checked} geprüft. Echter Karten-Scan noch ausstehend.")
                    return
            except (OSError, ValueError):
                pass
            time.sleep(2)
        raise ValueError("Agent oder Browser-Lebenszeichen fehlt")
    print("PC/SC-Leser geprüft. Agent-/Browserprüfung und echter Scan nach Einrichtung erforderlich.")


def configure_interrupted_packages():
    """dpkg --configure -a, waiting for a dpkg lock held by another APT run."""
    deadline = time.monotonic() + DPKG_LOCK_WAIT_SECONDS
    while True:
        try:
            # Package mutations must finish without a subprocess timeout.
            command("dpkg", "--configure", "-a", timeout=None, env={**os.environ, "LC_ALL": "C"})
            return
        except subprocess.CalledProcessError as error:
            if "lock" not in (error.stderr or "") or time.monotonic() >= deadline:
                raise
            time.sleep(5)


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
    try:
        # Finish a dpkg run interrupted by a killed activation before reinstalling.
        # This rollback unit intentionally has no systemd runtime limit.
        configure_interrupted_packages()
        originals = list((folder / "old").glob("*.deb"))
        if originals:
            command(*APT_INSTALL, "--allow-downgrades", "install", *(str(p) for p in originals), timeout=None)
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
    except Exception:
        # Even a failed rollback must not leave the terminal without NFC.
        if snapshot.get("agentActive"):
            subprocess.run(["systemctl", "start", AGENT_SERVICE], check=False)
        raise
    if snapshot["agentActive"]:
        command("systemctl", "start", AGENT_SERVICE)
    # The restored packages are the known-good state. A kiosk browser that
    # stopped sending heartbeats is not fixed by repeating the rollback.
    verify(folder, {**snapshot, "browserAlive": False}, require_fix=False)
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
            # The activation unit supplies its own outer deadline and rollback;
            # do not interrupt dpkg early with the timeout for read-only checks.
            command(*APT_INSTALL, "install", *(str(p) for p in (folder / "new").glob("*.deb")), timeout=None)
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
            write_result(folder, "failed", "Aktivierung fehlgeschlagen; Rückinstallation läuft")
            # A separate unit without the activation's time limit, so it is
            # never killed mid-dpkg; it starts once this unit releases the lock.
            try:
                detached(folder, snapshot["rollbackUnit"] + "-now", "--rollback")
            except Exception:
                print("Sofortige Rückinstallation nicht gestartet; Rollback-Timer übernimmt", file=sys.stderr)
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_mutually_exclusive_group(required=True)
    actions.add_argument("--check", action="store_true")
    actions.add_argument("--apply", action="store_true")
    actions.add_argument("--activate", type=Path, help=argparse.SUPPRESS)
    actions.add_argument("--rollback", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--auto", action="store_true", help="with --apply: skip if already attempted for this bundle")
    args = parser.parse_args()
    if args.check:
        # 0 = suitable, 1 = automatic migration pending or running, 2 = manual
        # action needed, 3 = no packages for this platform (nothing to do).
        if interrupted_migration():
            print("PC/SC-Paketmigration durch Neustart unterbrochen; Rückinstallation steht aus.")
            return 1
        if suitable():
            print("PC/SC-Fixstand geeignet (>= 2.5.0).")
            return 0
        if migration_running():
            print("PC/SC-Paketmigration läuft.")
            return 1
        if not platform_supported():
            print("PC/SC-Fixstand fehlt; Paketmigration für diese Plattform nicht verfügbar (nur Trixie/arm64).")
            return 3
        if auto_possible():
            print("PC/SC-Fixstand fehlt; automatische Paketmigration steht aus.")
            return 1
        print("PC/SC-Fixstand fehlt; automatische Migration bereits versucht oder nicht möglich (siehe Pi-Anleitung).")
        return 2
    if os.geteuid() != 0:
        raise ValueError("Paketmigration benötigt root")
    if args.activate:
        activate(args.activate.resolve())
    elif args.rollback:
        with locked():
            result = read_result(args.rollback)
            if result and result["status"] in FINISHED:
                return 0
            try:
                rollback(args.rollback, json.loads((args.rollback / "snapshot.json").read_text()))
            except Exception:
                write_result(args.rollback, "rollback-failed",
                             "Rückinstallation fehlgeschlagen; Originalsicherung und Journal prüfen")
                raise
    else:
        if recover_interrupted() and args.auto:
            return 0
        if args.auto:
            if suitable() or not platform_supported() or not auto_possible():
                print("Keine automatische Paketmigration erforderlich, möglich oder bereits versucht.")
                return 0
            try:
                folder = prepare()
            except ValueError:
                # A rejected system is not retried every updater run. Download
                # and APT errors are not recorded: they are often temporary
                # and occur before anything changed.
                record_auto_attempt(bundle_version())
                raise
        else:
            folder = prepare()
        if folder:
            return wait_for_result(folder)
    return 0


def recover_interrupted():
    """Rolls back a migration interrupted by a reboot; True if one was found."""
    with locked():
        folder = interrupted_migration()
        if folder is None:
            return False
        print("Paketmigration durch Neustart unterbrochen; Rückinstallation:", folder)
        snapshot = json.loads((folder / "snapshot.json").read_text())
        detached(folder, snapshot["rollbackUnit"] + "-recover", "--rollback")
    wait_for_result(folder)
    if read_result(folder)["status"] != "rolled-back":
        raise ValueError("Rückinstallation der unterbrochenen Migration fehlgeschlagen; Dienstjournal prüfen")
    return True


def wait_for_result(folder):
    reported = None
    deadline = time.monotonic() + RESULT_WAIT_SECONDS
    while time.monotonic() < deadline:
        result = read_result(folder)
        if result and result["status"] != reported:
            reported = result["status"]
            print(result["detail"])
        if result and result["status"] in TERMINAL:
            return 0 if result["status"] == "ok" else 1
        time.sleep(2)
    raise ValueError("Paketwechsel dauert länger; Dienstjournal prüfen; Rollback-Timer bleibt aktiv")


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("PC/SC-Migration fehlgeschlagen:", error, file=sys.stderr)
        sys.exit(1)
