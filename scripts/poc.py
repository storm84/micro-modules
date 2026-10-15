#!/usr/bin/env python3
"""Run the POC or verify its complete topology. Python standard library only."""
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
HOSTS = {"A": ("HostA", 5101), "C": ("HostC", 5102), "BIG": ("HostBig", 5103)}
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


def counter(port=5101, module="a"):
    result = post(port, module, "get-counter", {})
    return result if module == "c" else result["counter"]


class Runner:
    def __init__(self):
        self.processes = {}
        self.logs = {}
        LOGS.mkdir(parents=True, exist_ok=True)
        for host in HOSTS:
            (LOGS / f"{host}.log").write_text("")

    def prepare(self, build):
        for port in (5101, 5102, 5103, 5104):
            with socket.socket() as probe:
                try:
                    probe.bind(("127.0.0.1", port))
                except OSError as error:
                    raise RuntimeError(f"Port {port} is occupied. Stop the previous POC before launching another.") from error
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
        for host in ("C", "BIG", "A"):
            self.start(host)

    def start(self, host, primary=None):
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
            environment["MicroModules__C__Endpoints__0"] = primary
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
    """Test-only proxy: commit on C, then close the connection without delivering its response."""
    def do_POST(self):
        payload = self.rfile.read(int(self.headers["Content-Length"]))
        request = urllib.request.Request(f"http://127.0.0.1:5102{self.path}", data=payload,
                                         headers={"Content-Type": "application/json"}, method="POST")
        with HTTP.open(request, timeout=5) as response:
            response.read()  # C completed and committed before the caller sees a transport failure.
        self.close_connection = True
        self.connection.shutdown(socket.SHUT_RDWR)
        self.connection.close()

    def log_message(self, format, *args):
        pass


def verify(runner):
    for host, (_, port) in HOSTS.items():
        with HTTP.open(f"http://127.0.0.1:{port}/scalar", timeout=5) as response:
            assert response.status == 200 and "text/html" in response.headers["Content-Type"]
        with HTTP.open(f"http://127.0.0.1:{port}/openapi/v1.json", timeout=5) as response:
            document = json.load(response)
        expected = {"A": {"a"}, "C": {"c"}, "BIG": {"b", "c"}}[host]
        documented = {path.split("/")[2] for path in document["paths"] if path.startswith("/modules/")}
        assert documented == expected, (host, documented)
        for path, operations in document["paths"].items():
            if path.startswith("/modules/"):
                assert "requestBody" in operations["post"], (host, path)
                assert "200" in operations["post"]["responses"], (host, path)
    print("PASS: Scalar and OpenAPI document only the modules hosted by A, C, and BIG", flush=True)
    initial = counter()
    assert initial["servedBy"] == "C", initial
    baseline = initial["value"]
    local = counter(5103, "b")
    assert local == {"value": baseline, "servedBy": "BIG"}, local
    print("PASS: A→standalone C; B→C in BIG", flush=True)

    operation = {"operationId": str(uuid.uuid4()), "amount": 1}
    first = post(5101, "a", "increment-counter", operation)["counter"]
    replay = post(5103, "b", "increment-counter", operation)["counter"]
    assert first["value"] == replay["value"] == baseline + 1
    conflict = post(5103, "b", "increment-counter", {**operation, "amount": 2}, expected=409)
    assert conflict["code"] == "operation-id-conflict", conflict
    print("PASS: shared state, cross-host replay, conflicting operation ID", flush=True)

    def increment(index):
        port = 5102 if index % 2 == 0 else 5103
        return post(port, "c", "increment-counter", {"operationId": str(uuid.uuid4()), "amount": 1})
    with ThreadPoolExecutor(max_workers=8) as workers:
        list(workers.map(increment, range(24)))
    assert counter()["value"] == baseline + 25
    duplicate = {"operationId": str(uuid.uuid4()), "amount": 3}
    with ThreadPoolExecutor(max_workers=8) as workers:
        values = list(workers.map(lambda index: post(5102 if index % 2 == 0 else 5103, "c", "increment-counter", duplicate)["value"], range(8)))
    assert set(values) == {baseline + 28}, values
    assert counter()["value"] == baseline + 28
    print("PASS: concurrent writes and concurrent duplicate requests across both C hosts", flush=True)

    runner.stop("C")
    fallback = counter()
    assert fallback == {"value": baseline + 28, "servedBy": "BIG"}, fallback
    assert counter(5103, "b")["servedBy"] == "BIG"
    print("PASS: standalone C stopped; A fails over to C inside BIG", flush=True)
    runner.start("C")
    assert counter()["servedBy"] == "C"
    print("PASS: recovered standalone C is preferred again", flush=True)

    proxy = ThreadingHTTPServer(("127.0.0.1", 5104), LostResponseHandler)
    proxy.daemon_threads = True
    thread = threading.Thread(target=proxy.serve_forever, daemon=True)
    thread.start()
    try:
        runner.stop("A")
        runner.start("A", primary="http://127.0.0.1:5104")
        before = counter(5102, "c")["value"]
        result = post(5101, "a", "increment-counter", {"operationId": str(uuid.uuid4()), "amount": 1})["counter"]
        assert result == {"value": before + 1, "servedBy": "BIG"}, result
        assert counter(5102, "c")["value"] == before + 1
        print("PASS: C committed but response was lost; fallback returned its recorded result without another increment", flush=True)
    finally:
        runner.stop("A")
        proxy.shutdown()
        proxy.server_close()
        thread.join(timeout=2)
    assert "Module c operation get-counter mode local" in (LOGS / "BIG.log").read_text()
    print(f"All topology checks passed. Logs: {LOGS}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("run", "smoke"), nargs="?", default="run")
    parser.add_argument("--no-build", action="store_true")
    arguments = parser.parse_args()
    runner = Runner()
    def interrupt(*_):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupt)
    try:
        runner.prepare(not arguments.no_build)
        if arguments.mode == "smoke":
            verify(runner)
        else:
            print("Commands: stop-c, start-c, status, quit. Ctrl-C also stops all host processes.", flush=True)
            while True:
                try:
                    command = input("poc> ").strip().lower()
                except EOFError:
                    # Remain alive when launched without an interactive terminal.
                    while True:
                        time.sleep(1)
                if command == "quit":
                    break
                if command == "stop-c":
                    runner.stop("C")
                    print("Standalone C stopped. A will use BIG.", flush=True)
                elif command == "start-c":
                    runner.start("C")
                elif command == "status":
                    print(counter(), flush=True)
                elif command:
                    print("Commands: stop-c, start-c, status, quit", flush=True)
        return 0
    except KeyboardInterrupt:
        return 0
    except Exception as error:
        print(f"POC failed: {error}", file=sys.stderr)
        return 1
    finally:
        runner.close()
        print("Host processes stopped. Postgres data is retained; use docker compose down to stop the database.", flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
