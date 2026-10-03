"""Real API integration, called by run_e2e_test.sh with disposable settings."""
import hashlib
import json
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path

base, settings_path = sys.argv[1:]


def request(path, body=None, token=None, terminal="test-terminal", authorization=None):
    headers = {"Content-Type": "application/json"}
    if authorization is not None:
        headers["Authorization"] = authorization
    if token is not None:
        headers.update({"Authorization": "Bearer " + token, "X-Terminal-Id": terminal})
    req = urllib.request.Request(base + path, headers=headers,
                                 data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, error.read()


token = "integration-terminal-token"
diagnostics = {"agentVersion": "0.17.1", "uiStatus": "alive", "system": {"availableMemoryKb": 800000}}
assert request("/api/kiosk/diagnostics", diagnostics)[0] == 401
assert request("/api/kiosk/diagnostics", diagnostics, token="wrong")[0] == 401
assert request("/api/kiosk/diagnostics", {"padding": "x" * 33000}, token=token)[0] == 413
assert request("/api/kiosk/diagnostics", diagnostics, token=token)[0] == 200
assert request("/api/kiosk/diagnostics", diagnostics, token=token)[0] == 200
assert request("/api/kiosk/diagnostics", diagnostics, token=token)[0] == 429
assert request("/api/kiosk/catalog")[0] == 401
assert request("/api/kiosk/catalog", token="wrong")[0] == 401
status, catalog = request("/api/kiosk/catalog", token=token)
assert status == 200 and len(catalog) == 2
max_entry = next(e for e in catalog if e["employee"]["id"] == "test-max")
assert max_entry["verifier"] == hashlib.sha256((max_entry["salt"] + ":1234").encode()).hexdigest()
assert all("pin" not in e and "apiToken" not in e["employee"] for e in catalog)
event = {"eventId": str(uuid.uuid4()), "employeeId": "test-max", "action": "stop",
         "performedAt": "2026-09-28T18:00:00Z"}
status, result = request("/api/kiosk/clock/sync", {"events": [event]}, token=token)
assert status == 200 and result["results"][0]["status"] == "applied", result
assert request("/api/kiosk/clock/sync", {"events": [event]}, token=token)[1]["duplicates"] == 1
forged = dict(event, eventId=str(uuid.uuid4()), authenticatedTerminalId="test-terminal")
assert request("/api/kiosk/clock/sync", {"events": [forged]})[1]["results"][0]["status"] == "rejected"

path = Path(settings_path)
settings = json.loads(path.read_text())
settings["terminalTokens"]["test-terminal"] = "rotated-terminal-token"
path.write_text(json.dumps(settings))
assert request("/api/kiosk/catalog", token=token)[0] == 401
assert request("/api/kiosk/diagnostics", diagnostics, token=token)[0] == 401
assert request("/api/kiosk/clock/sync", {"events": [event]}, token=token)[0] == 401
assert request("/api/kiosk/catalog", token="rotated-terminal-token")[0] == 200
settings["terminalTokens"].clear()
path.write_text(json.dumps(settings))
assert request("/api/kiosk/catalog", token="rotated-terminal-token")[0] == 401
settings["terminalTokens"] = None
path.write_text(json.dumps(settings))
legacy = dict(event, eventId=str(uuid.uuid4()), pin="1234")
status, result = request("/api/kiosk/clock/sync", {"events": [legacy]}, authorization="Basic cHJveHk6c2VjcmV0")
assert status == 200 and result["results"][0]["status"] == "applied", result
assert request("/api/kiosk/clock/sync", {"events": []}, authorization="Bearer invalid")[0] == 401
assert request("/api/kiosk/catalog", token="rotated-terminal-token")[0] == 401
print("Terminal authentication integration passed")
