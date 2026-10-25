#!/usr/bin/env python3
"""The composition proof over the packed templates: the modular monolith and the service a kept package validation
generated, each published Host run as its own process on its own database of one PostgreSQL, both on one RabbitMQ. A
product created in a tenant through the modular monolith's Catalog must reach the service over the broker and be answered
as its stock item by the service's GetStockItem, so a wrong wire name, queue name or missing subscription in generated
content fails it. Run it on the directory of `validate.py --keep --output <dir>`; it starts and removes its own containers."""

import argparse
import base64
from dataclasses import dataclass
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import signal
import subprocess
import time
import urllib.error
import urllib.request
import uuid

import validate

# The images the Kit's fixtures and the generated smoke tests run.
POSTGRES = "postgres:18.6"
RABBITMQ = "rabbitmq:4.3.6"
# Each template the proof runs, and the EF Core Modules its database is migrated for: the Hosts apply no migration, and
# Ledger's Marten schema is applied when the modular monolith's Host starts (templates.md, "Running the generated Host").
MODULAR = ("pouryakit-modular-monolith", ("Catalog", "Billing", "Inventory"))
SERVICE = ("pouryakit-service", ("Inventory",))
ISSUER = "pouryakit-composition-proof"
TENANT = "north"
# How long the service may take to answer the stock item once the product is created.
DEADLINE = 60


@dataclass(frozen=True)
class Generated:
    """One generated solution of a kept validation: its template, the name it was generated with, its consumer folder."""
    template: str
    name: str
    folder: Path
    modules: tuple

    @property
    def solution(self):
        return self.folder / self.name

    @property
    def published(self):
        return self.folder / "publish" / f"{self.name}.Host.dll"

    @property
    def queue(self):
        """The Host's queue on the broker, as the template's `serviceName` symbol derives it from the name."""
        return re.sub(r"[^a-z0-9]+", "-", self.name.lower()).strip("-")


def generated(output):
    """The modular monolith and the service of the inventory's template consumers, as `validate.py --keep` left them."""
    inventory = json.loads((validate.ROOT / "scripts/packaging/inventory.json").read_text())
    found = []
    for template, modules in (MODULAR, SERVICE):
        consumer = next(consumer for consumer in inventory["compositions"] if consumer.get("template") == template)
        solution = Generated(template, consumer["generated"], output / "consumers" / consumer["name"], modules)
        if not solution.published.is_file():
            raise ValueError(f"{output} holds no {solution.published.relative_to(output)}: name the output directory of a "
                             "validate.py PASS run with --keep.")
        found.append(solution)
    return found


def token(key, now):
    """A caller's bearer token, HS256 over `key`, as each Host's Authentication:Schemes:Bearer settings verify it."""
    def encode(data):
        return base64.urlsafe_b64encode(data).rstrip(b"=").decode()

    claims = {"iss": ISSUER, "aud": ISSUER, "sub": "composition-proof", "tenant": TENANT, "nbf": now, "exp": now + 600}
    signed = ".".join(encode(json.dumps(part, separators=(",", ":")).encode()) for part in ({"alg": "HS256", "typ": "JWT"}, claims))
    return f"{signed}.{encode(hmac.new(key, signed.encode(), hashlib.sha256).digest())}"


def pass_line(modular, service, product, seconds, body, queues):
    return (f"PASS composition proof: product {product}, created in tenant {TENANT} through {modular.name} "
            f"({modular.template}), reached {service.name} ({service.template}) over one RabbitMQ holding the queues "
            f"{' and '.join(queues)}, each Host on its own database of one PostgreSQL; the service answered "
            f"GET /inventory/v2/stock-items/{product} 200 {body} {seconds:.1f} s after the create")


def docker(*arguments, check=True):
    result = subprocess.run(["docker", *arguments], capture_output=True, text=True)
    if check and result.returncode:
        raise RuntimeError(f"docker {' '.join(arguments)} failed: {result.stderr.strip()}")
    return result


def until(condition, seconds, failure):
    deadline = time.monotonic() + seconds
    while not (value := condition()):
        if time.monotonic() > deadline:
            raise RuntimeError(failure())
        time.sleep(0.5)
    return value


def start_container(name, image, port, environment):
    """The container on a free loopback port; returns that port."""
    variables = [argument for variable in environment for argument in ("-e", variable)]
    docker("run", "-d", "--rm", "--name", name, *variables, "-p", f"127.0.0.1::{port}", image)
    return docker("port", name, f"{port}/tcp").stdout.splitlines()[0].rsplit(":", 1)[1]


def queues(broker):
    """Each queue on the broker with its consumer count, but Wolverine's reply queues."""
    listed = docker("exec", broker, "rabbitmqctl", "--quiet", "list_queues", "name", "consumers", "--formatter", "json").stdout
    return {queue["name"]: queue["consumers"] for queue in json.loads(listed) if not queue["name"].startswith("wolverine.")}


def call(method, url, bearer, body=None, headers=None):
    """The status and body of one HTTP call, without any proxy of the environment."""
    request = urllib.request.Request(url, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Authorization": f"Bearer {bearer}", "Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request, timeout=10) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


class Host:
    """A generated Host's Release publish, run in Production on a loopback port of the system's choosing."""

    def __init__(self, solution, environment, log):
        self.solution, self.log = solution, log
        with log.open("w") as output:
            self.process = subprocess.Popen(["dotnet", solution.published.name], cwd=solution.published.parent,
                                            env={**os.environ, "ASPNETCORE_ENVIRONMENT": "Production",
                                                 "ASPNETCORE_URLS": "http://127.0.0.1:0", **environment},
                                            stdout=output, stderr=subprocess.STDOUT, start_new_session=True)

    def address(self):
        def listening():
            if self.process.poll() is not None:
                raise RuntimeError(f"{self.solution.name}.Host exited with {self.process.returncode}:\n{self.log.read_text()}")
            return re.search(r"Now listening on: (http://\S+)", self.log.read_text())
        return until(listening, 120, lambda: f"{self.solution.name}.Host never listened:\n{self.log.read_text()}").group(1)

    def stop(self):
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(15)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait()


def prove(output):
    modular, service = generated(output)
    evidence = output / "composition-proof"
    evidence.mkdir(exist_ok=True)
    key = secrets.token_bytes(32)
    bearer = token(key, int(time.time()))
    run_id = uuid.uuid4().hex[:8]
    postgres, broker = f"pouryakit-composition-postgres-{run_id}", f"pouryakit-composition-rabbitmq-{run_id}"
    hosts = []
    try:
        database_port = start_container(postgres, POSTGRES, 5432, ["POSTGRES_PASSWORD=composition-proof"])
        broker_port = start_container(broker, RABBITMQ, 5672, ["RABBITMQ_DEFAULT_USER=proof", "RABBITMQ_DEFAULT_PASS=proof"])
        until(lambda: docker("exec", postgres, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", check=False).returncode == 0, 60,
              lambda: f"{POSTGRES} never accepted connections")
        until(lambda: "Server startup complete" in docker("logs", broker).stdout, 120, lambda: f"{RABBITMQ} never started")
        environment = {
            "ConnectionStrings__broker": f"amqp://proof:proof@127.0.0.1:{broker_port}/",
            "Authentication__Schemes__Bearer__ValidIssuer": ISSUER,
            "Authentication__Schemes__Bearer__ValidAudiences__0": ISSUER,
            "Authentication__Schemes__Bearer__SigningKeys__0__Issuer": ISSUER,
            "Authentication__Schemes__Bearer__SigningKeys__0__Value": base64.b64encode(key).decode(),
        }
        addresses = {}
        # The service first: a message published before its queue is bound reaches no queue of it.
        for solution in (service, modular):
            settings = {**environment, "ConnectionStrings__app": f"Host=127.0.0.1;Port={database_port};Database={solution.queue.replace('-', '_')};"
                                                                  "Username=postgres;Password=composition-proof"}
            tools = validate.isolated_env(solution.folder)
            validate.run(["dotnet", "tool", "restore"], cwd=solution.solution, env=tools, log=evidence / f"{solution.name}-tool-restore.log")
            for module in solution.modules:
                validate.run(["dotnet", "ef", "database", "update", "--project", f"src/Modules/{module}/{module}",
                              "--startup-project", f"src/{solution.name}.Host", "--context", f"{module}DbContext",
                              "--configuration", "Release", "--no-build"],
                             cwd=solution.solution, env={**tools, **settings}, log=evidence / f"{solution.name}-migrate-{module}.log")
            host = Host(solution, settings, evidence / f"{solution.name}-host.log")
            hosts.append(host)
            addresses[solution] = host.address()
            until(lambda: queues(broker).get(solution.queue, 0) > 0, 60,
                  lambda: f"No consumer on the queue {solution.queue}; the broker holds {queues(broker)}")
        catalog, inventory = addresses[modular] + "/catalog/v2", addresses[service] + "/inventory/v2"
        category, product = str(uuid.uuid4()), str(uuid.uuid4())
        created = [call("POST", f"{catalog}/categories", bearer, {"categoryId": category, "name": "Composition"}),
                   call("POST", f"{catalog}/products", bearer, {"productId": product, "categoryId": category, "name": "Proof"},
                        {"Idempotency-Key": str(uuid.uuid4())})]
        if [status for status, _ in created] != [200, 201]:
            raise RuntimeError(f"{modular.name} refused the category or the product: {created}")
        started, answered = time.monotonic(), None

        def stock_item():
            nonlocal answered
            answered = call("GET", f"{inventory}/stock-items/{product}", bearer)
            return answered[0] == 200 and json.loads(answered[1]).get("productId") == product

        until(stock_item, DEADLINE, lambda: f"{service.name} did not answer the stock item of product {product} within {DEADLINE} s: "
                                            f"its last answer was {answered[0]} {answered[1]}; the broker holds {queues(broker)}")
        print(pass_line(modular, service, product, time.monotonic() - started, answered[1], sorted(queues(broker))), flush=True)
    finally:
        for host in hosts:
            host.stop()
        # With their anonymous volumes, which the images declare for their data.
        docker("rm", "-f", "-v", postgres, broker, check=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="The output directory of a validate.py PASS run with --keep.")
    arguments = parser.parse_args()
    output = arguments.output.resolve()
    try:
        prove(output)
    except (RuntimeError, ValueError, OSError) as error:
        logs = output / "composition-proof"
        print(f"FAIL: {error}" + (f"\nEach step's output is in {logs}." if logs.is_dir() else ""), flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
