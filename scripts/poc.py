#!/usr/bin/env python3
"""Run the parcel-booking sample or verify its complete topology. Python standard library only."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
LOGS = ROOT / "artifacts" / "poc"
HOSTS = {"API": ("BookingApi", 5101), "SHIPMENTS": ("ShipmentService", 5102), "ALL": ("AllInOne", 5103), "WEB": ("WebApp", 5105)}
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def post(port, module, operation, payload, expected=200):
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/modules/{module}/v1/{operation}",
        data=json.dumps(payload).encode(), headers={"Content-Type": "application/json"}, method="POST")
    try:
        with HTTP.open(request, timeout=8) as response:
            status, body = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, body = error.code, error.read()
    if status != expected:
        raise AssertionError(f"Expected HTTP {expected}, got {status}: {body.decode()}")
    return json.loads(body) if body else None


def web_request(path, payload=None, expected=200):
    request = urllib.request.Request("http://127.0.0.1:5105" + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Content-Type": "application/json"})
    try:
        with HTTP.open(request, timeout=8) as response:
            status, body = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, body = error.code, error.read()
    assert status == expected, (path, status, body.decode())
    return json.loads(body) if body else None


def booking_request():
    return {
        "operationId": str(uuid.uuid4()),
        "sender": {"name": "Demo Shop", "street": "Shop Street 1", "postalCode": "11122", "city": "Stockholm", "country": "SE"},
        "recipient": {"name": "Alex Example", "street": "Example Street 2", "postalCode": "41103", "city": "Gothenburg", "country": "SE"},
        "customerEmail": "alex@example.test", "weightGrams": 1500, "service": "standard",
    }


def details(shipment_id, port=5101):
    return post(port, "booking", "get-booking", {"shipmentId": shipment_id})


def await_notifications(shipment_id, count):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        result = details(shipment_id)
        if len(result["notifications"]) == count:
            return result
        time.sleep(0.1)
    raise AssertionError(f"Expected {count} notifications: {result}")


def await_web_message(shipment_id, message_id):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        result = web_request(f"/api/shipments/{shipment_id}")
        messages = [message for message in result["notifications"] if message["id"] == message_id]
        if messages:
            assert len(messages) == 1
            return messages[0]
        time.sleep(0.1)
    raise AssertionError(f"Message {message_id} did not reach the web inbox")


class Runner:
    def __init__(self, web_mode=None):
        self.web_mode = web_mode
        self.processes = {}
        self.logs = {}
        LOGS.mkdir(parents=True, exist_ok=True)

    def prepare(self, build):
        for port in (5101, 5102, 5103, 5104, 5105):
            with socket.socket() as probe:
                # Ignore TIME_WAIT sockets from a recently stopped demo.
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                try:
                    probe.bind(("127.0.0.1", port))
                except OSError as error:
                    raise RuntimeError(f"Port {port} is occupied. Stop the previous POC before launching another.") from error
        for host in HOSTS:
            (LOGS / f"{host}.log").write_text("")
        if build:
            with (LOGS / "build.log").open("w") as output:
                result = subprocess.run(["dotnet", "build", "MicroModules.sln"], cwd=ROOT, stdout=output, stderr=subprocess.STDOUT)
            if result.returncode:
                raise RuntimeError(f"Build failed; see {LOGS / 'build.log'}")
        print("Starting Postgres through Docker Compose…", flush=True)
        with (LOGS / "compose.log").open("w") as output:
            result = subprocess.run(["docker", "compose", "up", "-d", "--wait", "postgres"], cwd=ROOT,
                                    stdout=output, stderr=subprocess.STDOUT)
        if result.returncode:
            raise RuntimeError(f"Docker Compose failed; see {LOGS / 'compose.log'}")
        # Compose init scripts only run for a new volume. Apply this idempotent schema
        # explicitly so retained counter-demo databases also work without deleting data.
        for script in sorted((ROOT / "samples" / "database").glob("*.sql")):
            with script.open("rb") as schema:
                result = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql",
                                         "-U", "micro_modules", "-d", "micro_modules", "-v", "ON_ERROR_STOP=1"],
                                        cwd=ROOT, stdin=schema, capture_output=True)
            if result.returncode:
                raise RuntimeError(f"Schema initialization failed for {script.name}: {result.stderr.decode()}")
        for host in ("SHIPMENTS", "ALL", "API", "WEB"):
            self.start(host)

    def start(self, host, primary=None, outbox_enabled=None, web_mode=None, overrides=None):
        if host in self.processes and self.processes[host].poll() is None:
            print(f"{host} is already running.", flush=True)
            return
        project, port = HOSTS[host]
        directory = ROOT / "samples" / project
        assembly = directory / "bin" / "Debug" / "net10.0" / f"{project}.dll"
        if not assembly.exists():
            raise RuntimeError(f"Missing {assembly}; run the script without --no-build.")
        environment = os.environ.copy()
        environment["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
        environment["ASPNETCORE_ENVIRONMENT"] = "Development"
        if primary:
            for module in ("rates", "shipments", "notifications"):
                environment[f"MicroModules__{module}__Endpoints__0"] = primary
        if outbox_enabled is not None:
            environment["MicroModules__shipments__Outbox__Enabled"] = str(outbox_enabled).lower()
        if host == "WEB":
            mode = web_mode or self.web_mode
            if mode is not None:
                environment["MicroModules__shipments__Mode"] = "Remote" if mode == "remote" else "Local"
                environment["MicroModules__notifications__Mode"] = "Local" if mode == "local" else "Remote"
        if overrides:
            environment.update(overrides)
        output = (LOGS / f"{host}.log").open("a")
        self.logs[host] = output
        process = subprocess.Popen(["dotnet", str(assembly)], cwd=directory, env=environment,
                                   stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
        self.processes[host] = process
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"{host} exited with {process.returncode}; see {LOGS / f'{host}.log'}")
            try:
                with HTTP.open(f"http://127.0.0.1:{port}/health", timeout=1) as response:
                    if response.status == 200:
                        print(f"{host} ready at http://127.0.0.1:{port} (PID {process.pid})", flush=True)
                        return
            except (urllib.error.URLError, TimeoutError):
                time.sleep(0.1)
        raise RuntimeError(f"{host} did not become ready; see {LOGS / f'{host}.log'}")

    def stop(self, host):
        process = self.processes.pop(host, None)
        if process is not None and process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
        output = self.logs.pop(host, None)
        if output:
            output.close()

    def close(self):
        for host in list(self.processes):
            self.stop(host)


class LostResponseHandler(BaseHTTPRequestHandler):
    """Test-only proxy: commit on ShipmentService, then lose the response."""
    def do_POST(self):
        payload = self.rfile.read(int(self.headers["Content-Length"]))
        request = urllib.request.Request(f"http://127.0.0.1:5102{self.path}", data=payload,
                                         headers={"Content-Type": "application/json"}, method="POST")
        with HTTP.open(request, timeout=5) as response:
            response.read()  # The remote operation committed before the caller sees a transport failure.
        self.close_connection = True
        self.connection.shutdown(socket.SHUT_RDWR)
        self.connection.close()

    def log_message(self, format, *args):
        pass


def verify(runner):
    for host, (_, port) in HOSTS.items():
        if host == "WEB":
            with HTTP.open(f"http://127.0.0.1:{port}/", timeout=5) as response:
                assert response.status == 200 and "Parcel desk" in response.read().decode()
            post(port, "shipments", "list", {}, expected=404)
            continue
        with HTTP.open(f"http://127.0.0.1:{port}/scalar", timeout=5) as response:
            assert response.status == 200 and "text/html" in response.headers["Content-Type"]
        with HTTP.open(f"http://127.0.0.1:{port}/openapi/v1.json", timeout=5) as response:
            document = json.load(response)
        expected = {"API": {"booking"}, "SHIPMENTS": {"rates", "shipments", "notifications"},
                    "ALL": {"booking", "rates", "shipments", "notifications"}}[host]
        documented = {path.split("/")[2] for path in document["paths"] if path.startswith("/modules/")}
        assert documented == expected, (host, documented)
        for path, operations in document["paths"].items():
            if path.startswith("/modules/"):
                assert "requestBody" in operations["post"], (host, path)
                assert "200" in operations["post"]["responses"], (host, path)
        assert "BookParcelRequest" in document["components"]["schemas"]
    print("PASS: Scalar, request/response schemas, and module visibility", flush=True)

    quote = post(5101, "booking", "quote", {"weightGrams": 1500, "service": "standard"})
    assert quote["price"] == 69 and quote["currency"] == "SEK", quote
    for payload, code in [({"weightGrams": 0, "service": "standard"}, "invalid-weight"),
                          ({"weightGrams": 100, "service": "unknown"}, "unknown-service"),
                          ({"weightGrams": 100, "service": "standard", "destinationCountry": "NO"}, "unsupported-destination")]:
        assert post(5101, "booking", "quote", payload, expected=400)["code"] == code
    operation = booking_request()
    for invalid in [{**operation, "customerEmail": "bad"},
                    {**operation, "recipient": {**operation["recipient"], "postalCode": "123"}}]:
        post(5101, "booking", "book", invalid, expected=400)
    post(5101, "booking", "get-booking", {"shipmentId": str(uuid.uuid4())}, expected=404)
    first = post(5101, "booking", "book", operation)
    shipment_id = first["shipment"]["id"]
    assert first["servedBy"] == "ShipmentService" and first["shipment"]["quote"] == quote
    replay = post(5103, "booking", "book", operation)
    assert replay["servedBy"] == "AllInOne" and replay["shipment"] == first["shipment"]
    assert details(shipment_id, 5103)["shipment"] == first["shipment"]
    assert post(5103, "booking", "book", {**operation, "weightGrams": 2000}, expected=409)["code"] == "operation-id-conflict"
    assert await_notifications(shipment_id, 1)["notifications"][0]["kind"] == "booking-confirmation"
    print("PASS: quotes, validation, shared bookings, local/remote calls, and operation replay", flush=True)

    duplicate = booking_request()
    with ThreadPoolExecutor(max_workers=8) as workers:
        results = list(workers.map(lambda index: post(5102 if index % 2 == 0 else 5103,
                                                    "shipments", "book", duplicate), range(8)))
    assert len({result["shipment"]["id"] for result in results}) == 1
    assert len(await_notifications(results[0]["shipment"]["id"], 1)["notifications"]) == 1
    # Independent bookings also commit safely under concurrent writes.
    with ThreadPoolExecutor(max_workers=8) as workers:
        independent = list(workers.map(lambda index: post(5102 if index % 2 == 0 else 5103,
                                                         "shipments", "book", booking_request()), range(12)))
    assert len({result["shipment"]["id"] for result in independent}) == 12
    post(5101, "booking", "simulate-tracking", {"operationId": str(uuid.uuid4()),
         "shipmentId": shipment_id, "status": "Delivered"}, expected=409)
    collected = {"operationId": str(uuid.uuid4()), "shipmentId": shipment_id, "status": "Collected"}
    with ThreadPoolExecutor(max_workers=8) as workers:
        updates = list(workers.map(lambda index: post(5102 if index % 2 == 0 else 5103,
                                                     "shipments", "simulate-tracking", collected), range(8)))
    assert all(len(update["shipment"]["events"]) == 2 for update in updates)
    post(5101, "booking", "simulate-tracking", {**collected, "status": "InTransit"}, expected=409)
    for status in ("InTransit", "Delivered"):
        post(5101, "booking", "simulate-tracking", {"operationId": str(uuid.uuid4()),
             "shipmentId": shipment_id, "status": status})
    completed = await_notifications(shipment_id, 2)
    assert [event["status"] for event in completed["shipment"]["events"]] == ["Booked", "Collected", "InTransit", "Delivered"]
    assert {message["kind"] for message in completed["notifications"]} == {"booking-confirmation", "delivered"}
    # A replay after later transitions still returns its original immutable result.
    assert post(5101, "booking", "book", operation)["shipment"] == first["shipment"]
    assert post(5101, "booking", "simulate-tracking", collected)["shipment"]["status"] == "Collected"
    post(5101, "booking", "simulate-tracking", {"operationId": str(uuid.uuid4()),
         "shipmentId": shipment_id, "status": "Delivered"}, expected=409)
    print("PASS: concurrent writes, duplicate bookings/tracking, lifecycle rules, and exactly two notifications", flush=True)

    page = web_request("/api/shipments?limit=1")
    assert len(page["shipments"]) == 1 and page["hasMore"] and page["servedBy"] == "ShipmentService"
    second_page = web_request("/api/shipments?offset=1&limit=1")
    assert second_page["shipments"][0]["id"] != page["shipments"][0]["id"]
    web_request("/api/shipments?limit=101", expected=400)
    assert web_request(f"/api/shipments/{shipment_id}")["shipment"]["id"] == shipment_id
    web_request(f"/api/shipments/{uuid.uuid4()}", expected=404)
    draft = {"operationId": str(uuid.uuid4()), "subject": "A personal update", "body": "Your parcel arrived safely."}
    web_request(f"/api/shipments/{shipment_id}/notifications", {**draft, "subject": " "}, expected=400)
    web_request(f"/api/shipments/{uuid.uuid4()}/notifications", draft, expected=404)
    queued = web_request(f"/api/shipments/{shipment_id}/notifications", draft, expected=202)
    assert queued["email"] == operation["customerEmail"] and queued["id"] == draft["operationId"]
    assert web_request(f"/api/shipments/{shipment_id}/notifications", draft, expected=202) == queued
    web_request(f"/api/shipments/{shipment_id}/notifications", {**draft, "body": "Changed"}, expected=409)
    custom = await_notifications(shipment_id, 3)["notifications"]
    assert len([message for message in custom if message["kind"] == "custom"]) == 1
    print("PASS: remote-only web app, paged shipments, details, and durable custom notifications", flush=True)

    runner.stop("SHIPMENTS")
    assert details(shipment_id)["servedBy"] == "AllInOne"
    assert web_request("/api/shipments")["servedBy"] == "AllInOne"
    fallback = post(5101, "booking", "book", booking_request())
    assert fallback["servedBy"] == "AllInOne"
    runner.start("SHIPMENTS")
    assert details(shipment_id)["servedBy"] == "ShipmentService"
    assert web_request("/api/shipments")["servedBy"] == "ShipmentService"
    print("PASS: failover preserves bookings/history; recovered preferred service is selected", flush=True)

    # Pause both application workers, commit a pending notification, and restart them.
    for host in ("SHIPMENTS", "ALL"):
        runner.stop(host)
        runner.start(host, outbox_enabled=False)
    pending = post(5101, "booking", "book", booking_request())["shipment"]["id"]
    assert details(pending)["notifications"] == []
    for host in ("SHIPMENTS", "ALL"):
        runner.stop(host)
        runner.start(host)
    assert len(await_notifications(pending, 1)["notifications"]) == 1
    print("PASS: pending notifications survive process restarts and competing workers", flush=True)

    proxy = ThreadingHTTPServer(("127.0.0.1", 5104), LostResponseHandler)
    proxy.daemon_threads = True
    thread = threading.Thread(target=proxy.serve_forever, daemon=True)
    thread.start()
    try:
        runner.stop("API")
        runner.start("API", primary="http://127.0.0.1:5104")
        lost = booking_request()
        result = post(5101, "booking", "book", lost)
        assert result["servedBy"] == "AllInOne", result
        assert post(5102, "shipments", "book", lost)["shipment"] == result["shipment"]
        runner.stop("API")
        runner.start("API")
        assert len(await_notifications(result["shipment"]["id"], 1)["notifications"]) == 1
        print("PASS: lost response after commit replays one booking and one notification", flush=True)
        runner.stop("WEB")
        runner.start("WEB", primary="http://127.0.0.1:5104")
        lost_draft = {"operationId": str(uuid.uuid4()), "subject": "Delivery update", "body": "A safe retry."}
        lost_id = result["shipment"]["id"]
        queued = web_request(f"/api/shipments/{lost_id}/notifications", lost_draft, expected=202)
        assert queued["id"] == lost_draft["operationId"]
        assert web_request(f"/api/shipments/{lost_id}/notifications", lost_draft, expected=202) == queued
        assert len(await_notifications(lost_id, 2)["notifications"]) == 2
        runner.stop("WEB")
        runner.start("WEB")
        print("PASS: lost custom-notification response replays one queued message through the web app", flush=True)
    finally:
        proxy.shutdown()
        proxy.server_close()
        thread.join(timeout=2)
    assert "Module shipments operation book mode local" in (LOGS / "ALL.log").read_text()
    assert "http-client" in (LOGS / "API.log").read_text()
    web_logs = (LOGS / "WEB.log").read_text()
    assert "http-client" in web_logs and " mode local" not in web_logs
    runner.stop("SHIPMENTS")
    runner.stop("ALL")
    assert web_request("/api/shipments", expected=503)["code"] == "services-unavailable"
    for host in ("SHIPMENTS", "ALL"):
        runner.start(host)
    print("PASS: web requests use HTTP only and return a clear error when remote services are unavailable", flush=True)
    # Change composition through configuration, keeping the web adapters identical.
    for host in ("API", "SHIPMENTS", "ALL", "WEB"):
        runner.stop(host)
    runner.start("WEB", web_mode="local")
    assert web_request("/api/shipments")["servedBy"] == "WebApp"
    assert web_request(f"/api/shipments/{shipment_id}")["servedBy"] == "WebApp"
    local_draft = {"operationId": str(uuid.uuid4()), "subject": "Local update", "body": "No other application hosts are running."}
    web_request(f"/api/shipments/{shipment_id}/notifications", local_draft, expected=202)
    assert await_web_message(shipment_id, local_draft["operationId"])["body"] == local_draft["body"]
    print("PASS: fully local WebApp handles shipments and notification delivery with every other host stopped", flush=True)
    runner.stop("WEB")
    # Only WebApp's worker should claim this row, proving its remote delivery path.
    runner.start("SHIPMENTS", outbox_enabled=False)
    runner.start("WEB", web_mode="mixed", overrides={
        "MicroModules__notifications__ConnectionString": "invalid-but-unused",
        "MicroModules__rates__Mode": "Remote",
        "MicroModules__rates__Endpoints__0": "http://127.0.0.1:5102",
    })
    assert web_request("/api/shipments")["servedBy"] == "WebApp"
    mixed_draft = {"operationId": str(uuid.uuid4()), "subject": "Mixed update", "body": "Local shipment, remote notification delivery."}
    web_request(f"/api/shipments/{shipment_id}/notifications", mixed_draft, expected=202)
    assert await_web_message(shipment_id, mixed_draft["operationId"])["body"] == mixed_draft["body"]
    final_web_logs = (LOGS / "WEB.log").read_text()
    assert "Module shipments operation list mode local" in final_web_logs
    assert "Module notifications operation deliver mode http-client" in final_web_logs, "Mixed WebApp did not dispatch through remote Notifications"
    print("PASS: mixed WebApp uses local Shipments and remote Notifications without initializing notification storage", flush=True)
    # Exercise failures around notification acknowledgement in an isolated database schema.
    environment = os.environ.copy()
    environment["PARCEL_TEST_CONNECTION"] = "Host=127.0.0.1;Port=55432;Database=micro_modules;Username=micro_modules;Password=micro_modules_dev"
    result = subprocess.run(["dotnet", "test", "tests/Parcel.Tests/Parcel.Tests.csproj", "--no-build", "--nologo"],
                            cwd=ROOT, env=environment)
    if result.returncode:
        raise AssertionError("Parcel database tests failed")
    print(f"All parcel topology checks passed. Logs: {LOGS}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("run", "smoke"), nargs="?", default="run")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--web-mode", choices=("remote", "local", "mixed"),
                        help="Override WebApp configuration (smoke always verifies all three modes)")
    arguments = parser.parse_args()
    runner = Runner(arguments.web_mode if arguments.mode == "run" else "remote")
    def interrupt(*_):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupt)
    try:
        runner.prepare(not arguments.no_build)
        print("Web app: http://127.0.0.1:5105 — view shipments and send custom notifications.", flush=True)
        if arguments.mode == "smoke":
            verify(runner)
        else:
            print("Commands: demo, stop-shipments, start-shipments, status, quit. Ctrl-C also stops all host processes.", flush=True)
            while True:
                try:
                    command = input("parcel> ").strip().lower()
                except EOFError:
                    # Remain alive when launched without an interactive terminal.
                    while True:
                        time.sleep(1)
                if command == "quit":
                    break
                if command == "stop-shipments":
                    runner.stop("SHIPMENTS")
                    print("ShipmentService stopped. BookingApi will use AllInOne.", flush=True)
                elif command == "start-shipments":
                    runner.start("SHIPMENTS")
                elif command == "status":
                    print({host: process.poll() is None for host, process in runner.processes.items()}, flush=True)
                elif command == "demo":
                    request = booking_request()
                    print("Quote:", post(5101, "booking", "quote", {"weightGrams": request["weightGrams"], "service": request["service"]}), flush=True)
                    booked = post(5101, "booking", "book", request)
                    shipment_id = booked["shipment"]["id"]
                    print("Booked:", json.dumps(booked, indent=2), flush=True)
                    confirmation = await_notifications(shipment_id, 1)
                    print("Booking notification:", json.dumps(confirmation["notifications"], indent=2), flush=True)
                    for status in ("Collected", "InTransit", "Delivered"):
                        post(5101, "booking", "simulate-tracking", {"operationId": str(uuid.uuid4()), "shipmentId": shipment_id, "status": status})
                    print("Delivered:", json.dumps(await_notifications(shipment_id, 2), indent=2), flush=True)
                elif command:
                    print("Commands: demo, stop-shipments, start-shipments, status, quit", flush=True)
        return 0
    except KeyboardInterrupt:
        return 0
    except Exception as error:
        print(f"Parcel demo failed: {error}", file=sys.stderr)
        return 1
    finally:
        runner.close()
        print("Host processes stopped. Postgres data is retained; use docker compose down to stop the database.", flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
