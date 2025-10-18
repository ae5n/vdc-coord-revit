import sys
import traceback
from typing import Any, Callable, Dict
from uuid import uuid4

from core.ipc.namedpipe import PipeServer
from core import logger
from tools.create_views.app import handle as handle_create_views
from tools.level_report.app import handle as handle_level_report
from tools.grid_report.app import handle as handle_grid_report

PIPE_NAME = "RevitSuitePipe"
HANDLERS: Dict[str, Callable[[Dict[str, Any], str], Dict[str, Any]]] = {
    "create_views": handle_create_views,
    "level_report": handle_level_report,
    "grid_report": handle_grid_report,
}


def main() -> None:
    server = PipeServer(PIPE_NAME)
    print(f"Engine ready on \\\\.\\pipe\\{PIPE_NAME}")

    for request, respond in server.listen():
        correlation_id = request.get("correlationId") or uuid4().hex
        method = request.get("method")
        payload = request.get("payload", {})

        if not method:
            respond(
                {
                    "ok": False,
                    "error": "Request missing method.",
                    "correlationId": correlation_id,
                }
            )
            continue

        handler = HANDLERS.get(method)
        if handler is None:
            logger.warn("engine", correlation_id, f"Unknown method '{method}'.")
            respond(
                {
                    "ok": False,
                    "error": f"Unknown method: {method}",
                    "correlationId": correlation_id,
                }
            )
            continue

        try:
            result = handler(payload, correlation_id)
            logger.info("engine", correlation_id, f"{method} succeeded.")
            respond(
                {
                    "ok": True,
                    "result": result,
                    "correlationId": correlation_id,
                }
            )
        except Exception as exc:  # pylint: disable=broad-except
            tb = "".join(traceback.format_exception(exc.__class__, exc, exc.__traceback__))
            logger.error("engine", correlation_id, f"{method} failed.", exc)
            respond(
                {
                    "ok": False,
                    "error": str(exc),
                    "correlationId": correlation_id,
                    "traceback": tb if __debug__ else None,
                }
            )


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
