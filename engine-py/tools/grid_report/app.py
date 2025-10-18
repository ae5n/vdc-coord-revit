import csv
from pathlib import Path
from typing import Any, Dict, List, Optional

from core import logger

REQUIRED_GRID_KEYS = {
    "model",
    "modelId",
    "type",
    "name",
    "curveType",
    "lengthFt",
    "angleDeg",
    "radiusFt",
    "startX",
    "startY",
    "startZ",
    "endX",
    "endY",
    "endZ",
    "gridId",
    "gridUniqueId",
}


def handle(payload: Dict[str, Any], correlation_id: str) -> Dict[str, Any]:
    data = _validate(payload)
    target_path = Path(data["targetPath"]).expanduser()
    precision = data["precision"]
    preview_rows = data["maxPreviewRows"]
    grids = data["grids"]

    target_path.parent.mkdir(parents=True, exist_ok=True)

    sorted_grids = sorted(
        grids,
        key=lambda item: (
            item["model"].lower(),
            item["name"].lower(),
            item["type"].lower(),
        ),
    )

    csv_rows = [
        {
            "Model": grid["model"],
            "Type": grid["type"],
            "Grid": grid["name"],
            "CurveType": grid["curveType"],
            "Length_ft": _fmt(_round(grid.get("lengthFt"), precision)),
            "Angle_deg": _fmt(_round(grid.get("angleDeg"), precision)),
            "Radius_ft": _fmt(_round(grid.get("radiusFt"), precision)),
            "Start_X_ft": _fmt(_round(grid.get("startX"), precision)),
            "Start_Y_ft": _fmt(_round(grid.get("startY"), precision)),
            "Start_Z_ft": _fmt(_round(grid.get("startZ"), precision)),
            "End_X_ft": _fmt(_round(grid.get("endX"), precision)),
            "End_Y_ft": _fmt(_round(grid.get("endY"), precision)),
            "End_Z_ft": _fmt(_round(grid.get("endZ"), precision)),
            "GridId": grid["gridId"],
            "GridUniqueId": grid["gridUniqueId"],
            "ModelId": grid["modelId"],
        }
        for grid in sorted_grids
    ]

    with target_path.open("w", newline="", encoding="utf-8") as handle_out:
        writer = csv.DictWriter(
            handle_out,
            fieldnames=[
                "Model",
                "Type",
                "Grid",
                "CurveType",
                "Length_ft",
                "Angle_deg",
                "Radius_ft",
                "Start_X_ft",
                "Start_Y_ft",
                "Start_Z_ft",
                "End_X_ft",
                "End_Y_ft",
                "End_Z_ft",
                "GridId",
                "GridUniqueId",
                "ModelId",
            ],
        )
        writer.writeheader()
        writer.writerows(csv_rows)

    preview = csv_rows[:preview_rows]
    result = {
        "written": str(target_path),
        "rows": len(csv_rows),
        "preview": preview,
    }

    logger.info(
        "engine",
        correlation_id,
        "grid_report generated.",
        extra={"written": str(target_path), "rows": len(csv_rows)},
    )

    return result


def _validate(payload: Dict[str, Any]) -> Dict[str, Any]:
    if not isinstance(payload, dict):
        raise ValueError("Payload must be an object.")

    target_path = payload.get("targetPath")
    if not isinstance(target_path, str) or not target_path.strip():
        raise ValueError("targetPath must be a non-empty string.")

    include_linked = payload.get("includeLinkedModels", True)
    if not isinstance(include_linked, bool):
        raise ValueError("includeLinkedModels must be a boolean.")

    precision_raw = payload.get("precision", 2)
    try:
        precision = int(precision_raw)
    except (TypeError, ValueError) as exc:
        raise ValueError("precision must be an integer.") from exc
    if precision < 0 or precision > 6:
        raise ValueError("precision must be between 0 and 6.")

    preview_rows_raw = payload.get("maxPreviewRows", 5)
    try:
        preview_rows = int(preview_rows_raw)
    except (TypeError, ValueError) as exc:
        raise ValueError("maxPreviewRows must be an integer.") from exc
    if preview_rows < 0 or preview_rows > 20:
        raise ValueError("maxPreviewRows must be between 0 and 20.")

    grids = payload.get("grids", [])
    if not isinstance(grids, list):
        raise ValueError("grids must be a list.")

    validated: List[Dict[str, Any]] = []

    for index, entry in enumerate(grids):
        if not isinstance(entry, dict):
            raise ValueError(f"grids[{index}] must be an object.")

        missing = REQUIRED_GRID_KEYS - entry.keys()
        if missing:
            raise ValueError(f"grids[{index}] missing keys: {sorted(missing)}")

        validated.append(
            {
                "model": _require_str(entry["model"], f"grids[{index}].model"),
                "modelId": _require_str(entry["modelId"], f"grids[{index}].modelId"),
                "type": _require_str(entry["type"], f"grids[{index}].type"),
                "name": _require_str(entry["name"], f"grids[{index}].name"),
                "curveType": _require_str(entry["curveType"], f"grids[{index}].curveType"),
                "lengthFt": _require_float(entry.get("lengthFt"), f"grids[{index}].lengthFt"),
                "angleDeg": _optional_float(entry.get("angleDeg"), f"grids[{index}].angleDeg"),
                "radiusFt": _optional_float(entry.get("radiusFt"), f"grids[{index}].radiusFt"),
                "startX": _optional_float(entry.get("startX"), f"grids[{index}].startX"),
                "startY": _optional_float(entry.get("startY"), f"grids[{index}].startY"),
                "startZ": _optional_float(entry.get("startZ"), f"grids[{index}].startZ"),
                "endX": _optional_float(entry.get("endX"), f"grids[{index}].endX"),
                "endY": _optional_float(entry.get("endY"), f"grids[{index}].endY"),
                "endZ": _optional_float(entry.get("endZ"), f"grids[{index}].endZ"),
                "gridId": _require_int(entry["gridId"], f"grids[{index}].gridId"),
                "gridUniqueId": _require_str(entry["gridUniqueId"], f"grids[{index}].gridUniqueId"),
            }
        )

    return {
        "schemaVersion": str(payload.get("schemaVersion", "1.0.0")),
        "includeLinkedModels": include_linked,
        "precision": precision,
        "maxPreviewRows": preview_rows,
        "targetPath": target_path.strip(),
        "grids": validated,
    }


def _require_str(value: Any, label: str) -> str:
    text = "" if value is None else str(value)
    if not text.strip():
        raise ValueError(f"{label} cannot be empty.")
    return text.strip()


def _require_int(value: Any, label: str) -> int:
    try:
        return int(value)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{label} must be an integer.") from exc


def _require_float(value: Any, label: str) -> float:
    try:
        return float(value)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{label} must be a number.") from exc


def _optional_float(value: Any, label: str) -> Optional[float]:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{label} must be a number if provided.") from exc


def _round(value: Optional[float], precision: int) -> Optional[float]:
    if value is None:
        return None
    return round(float(value), precision)


def _fmt(value: Optional[float]) -> Any:
    if value is None:
        return ""
    return value
