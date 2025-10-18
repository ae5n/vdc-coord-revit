import json
import os
from datetime import datetime
from pathlib import Path
from threading import Lock
from typing import Any, Dict, Optional

_lock = Lock()


def _log_directory() -> Path:
    base = os.getenv("LOCALAPPDATA")
    if base:
        return Path(base) / "RevitSuite" / "logs"
    return Path.home() / ".revit-suite" / "logs"


def _log_file(prefix: str) -> Path:
    directory = _log_directory()
    directory.mkdir(parents=True, exist_ok=True)
    return directory / f"{prefix}-{datetime.utcnow():%Y%m%d}.log"


def _write(prefix: str, level: str, correlation_id: str, message: str, extra: Optional[Dict[str, Any]] = None, error: Optional[str] = None) -> None:
    payload = {
        "ts": datetime.utcnow().isoformat() + "Z",
        "level": level,
        "corr": correlation_id,
        "message": message,
    }
    if extra:
        payload["extra"] = extra
    if error:
        payload["error"] = error

    log_path = _log_file(prefix)

    line = json.dumps(payload, ensure_ascii=False)

    with _lock:
        with log_path.open("a", encoding="utf-8") as handle:
            handle.write(line + "\n")


def info(prefix: str, correlation_id: str, message: str, extra: Optional[Dict[str, Any]] = None) -> None:
    _write(prefix, "INFO", correlation_id, message, extra=extra)


def warn(prefix: str, correlation_id: str, message: str, extra: Optional[Dict[str, Any]] = None) -> None:
    _write(prefix, "WARN", correlation_id, message, extra=extra)


def error(prefix: str, correlation_id: str, message: str, exc: Optional[BaseException] = None) -> None:
    error_text = repr(exc) if exc else None
    _write(prefix, "ERROR", correlation_id, message, error=error_text)
