"""Serve BasiliskSimulationService: python -m argus_basilisk [--endpoint host:port]."""

import argparse
import ipaddress
import os
from concurrent.futures import ThreadPoolExecutor

import grpc

from .contracts import service_grpc
from .servicer import BasiliskSimulationServicer

DEFAULT_ENDPOINT = "127.0.0.1:50051"
ENDPOINT_VARIABLE = "ARGUS_BASILISK_ENDPOINT"


def parse_endpoint(parser: argparse.ArgumentParser, value: str) -> str:
    """Accept host:port or [ipv6]:port on a loopback host; v1 serves an insecure channel."""
    if value.startswith("["):
        host, separator, port = value[1:].partition("]:")
    else:
        host, separator, port = value.rpartition(":")
    if not separator or not host:
        parser.error(f"endpoint must be host:port, got {value!r}")

    try:
        port_number = int(port)
    except ValueError:
        parser.error(f"endpoint port must be a number, got {port!r}")
    if not 1 <= port_number <= 65535:
        parser.error(f"endpoint port must be 1-65535, got {port_number}")

    if host != "localhost":
        try:
            is_loopback = ipaddress.ip_address(host).is_loopback
        except ValueError:
            is_loopback = False
        if not is_loopback:
            parser.error("v1 serves loopback only (insecure channel)")
    return value


def main() -> None:
    parser = argparse.ArgumentParser(prog="argus_basilisk", description=__doc__)
    parser.add_argument(
        "--endpoint",
        help=f"host:port to serve on (default: ${ENDPOINT_VARIABLE}, then {DEFAULT_ENDPOINT})")
    arguments = parser.parse_args()
    endpoint = parse_endpoint(
        parser, arguments.endpoint or os.environ.get(ENDPOINT_VARIABLE) or DEFAULT_ENDPOINT)

    # One run and one client per process: a concurrent call gets RESOURCE_EXHAUSTED.
    server = grpc.server(ThreadPoolExecutor(max_workers=1), maximum_concurrent_rpcs=1)
    service_grpc.add_BasiliskSimulationServiceServicer_to_server(BasiliskSimulationServicer(), server)
    try:
        port = server.add_insecure_port(endpoint)
    except RuntimeError as error:
        raise SystemExit(f"Cannot bind {endpoint}: {error}") from error
    if port == 0:
        raise SystemExit(f"Cannot bind {endpoint}")

    server.start()
    print(f"BasiliskSimulationService listening on {endpoint}", flush=True)
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        server.stop(None)


if __name__ == "__main__":
    main()
