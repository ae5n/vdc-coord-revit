from typing import Any, Dict

from core import logger

SUPPORTED_VIEW_TYPES = {"FloorPlan", "CeilingPlan"}


def handle(payload: Dict[str, Any], correlation_id: str) -> Dict[str, Any]:
    data = _validate(payload)

    level_name = data["levelName"]
    view_type = data["viewType"]
    scale = data["scale"]

    logger.info(
        "engine",
        correlation_id,
        "create_views plan generated.",
        extra={"levelName": level_name, "viewType": view_type, "scale": scale},
    )

    plan = {
        "actions": [
            {
                "createView": {
                    "name": f"Plan - {level_name}",
                    "levelName": level_name,
                    "type": view_type,
                    "scale": scale,
                }
            }
        ]
    }

    return plan


def _validate(payload: Dict[str, Any]) -> Dict[str, Any]:
    if not isinstance(payload, dict):
        raise ValueError("Payload must be an object.")

    level_name = payload.get("levelName")
    if not isinstance(level_name, str) or not level_name.strip():
        raise ValueError("levelName must be a non-empty string.")

    view_type = payload.get("viewType")
    if view_type not in SUPPORTED_VIEW_TYPES:
        raise ValueError(f"viewType must be one of {sorted(SUPPORTED_VIEW_TYPES)}.")

    scale_raw = payload.get("scale", 96)
    try:
        scale = int(scale_raw)
    except (TypeError, ValueError) as exc:
        raise ValueError("scale must be an integer.") from exc

    if scale < 1:
        raise ValueError("scale must be a positive integer.")

    return {
        "schemaVersion": str(payload.get("schemaVersion", "1.0.0")),
        "levelName": level_name.strip(),
        "viewType": view_type,
        "scale": scale,
    }
