"""Serve BasiliskSimulationService: python -m argus_basilisk [--endpoint host:port] [--stop-on-stdin-eof]."""

import argparse
import ipaddress
import os
import signal
import sys
import threading
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


def build_server(servicer: BasiliskSimulationServicer | None = None) -> grpc.Server:
    """The service's gRPC server, neither bound nor started.

    A caller binds it; v1 is an insecure channel, so bind loopback only, as main() does through parse_endpoint.
    """
    # Basilisk never runs two calls at once: the servicer's lock refuses a call that reaches it while another
    # runs (RESOURCE_EXHAUSTED). gRPC's maximum_concurrent_rpcs is not used: gRPC counts a call until its worker
    # has freed the response, after sending it, so a strictly sequential client was refused under load (BSK-16).
    # The second worker lets an overlapping call reach that lock instead of waiting behind the first.
    server = grpc.server(ThreadPoolExecutor(max_workers=2))
    service_grpc.add_BasiliskSimulationServiceServicer_to_server(servicer or BasiliskSimulationServicer(), server)
    return server


def start_stdin_watch(server: grpc.Server) -> threading.Thread:
    """Stop the server once standard input reaches end of file.

    The parent holds the other end of the pipe, so it closes when the parent closes it or exits, even if the
    parent is killed: a service started by headless/Host never outlives it (README §2).
    """
    def watch() -> None:
        while sys.stdin.buffer.read(65536):
            pass
        server.stop(None)

    thread = threading.Thread(target=watch, name="stdin-eof", daemon=True)
    thread.start()
    return thread


def main() -> None:
    parser = argparse.ArgumentParser(prog="argus_basilisk", description=__doc__)
    parser.add_argument(
        "--endpoint",
        help=f"host:port to serve on (default: ${ENDPOINT_VARIABLE}, then {DEFAULT_ENDPOINT})")
    parser.add_argument(
        "--stop-on-stdin-eof",
        action="store_true",
        help="stop when standard input closes, so the service ends with the process that started it "
             "(headless/Host --start-basilisk); without it the service runs until Ctrl+C")
    arguments = parser.parse_args()
    endpoint = parse_endpoint(
        parser, arguments.endpoint or os.environ.get(ENDPOINT_VARIABLE) or DEFAULT_ENDPOINT)

    server = build_server()
    try:
        port = server.add_insecure_port(endpoint)
    except RuntimeError as error:
        raise SystemExit(f"Cannot bind {endpoint}: {error}") from error
    if port == 0:
        raise SystemExit(f"Cannot bind {endpoint}")

    server.start()
    if arguments.stop_on_stdin_eof:
        # Ctrl+C in the parent's terminal reaches this process too. The parent stops it by closing
        # stdin once its run has ended, so an interrupt here would only cut that run short.
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        start_stdin_watch(server)
    print(f"BasiliskSimulationService listening on {endpoint}", flush=True)
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        server.stop(None)


if __name__ == "__main__":
    main()
