import csv
from pathlib import Path
from typing import Any, Dict, List

from core import logger

REQUIRED_LEVEL_KEYS = {
    "model",
    "modelId",
    "type",
    "level",
    "elevationFt",
    "levelId",
    "levelUniqueId",
}


def handle(payload: Dict[str, Any], correlation_id: str) -> Dict[str, Any]:
    data = _validate(payload)
    target_path = Path(data["targetPath"]).expanduser()
    precision = data["precision"]
    preview_rows = data["maxPreviewRows"]
    levels = data["levels"]

    target_path.parent.mkdir(parents=True, exist_ok=True)

    sorted_levels = sorted(
        levels,
        key=lambda item: (
            item["model"].lower(),
            item["elevationFt"],
            item["level"].lower(),
        ),
    )

    csv_rows = [
        {
            "Model": level["model"],
            "Type": level["type"],
            "Level": level["level"],
            "Elevation_ft": round(level["elevationFt"], precision),
            "Elevation_ft_in": _format_feet_inches(level["elevationFt"], precision),
            "LevelId": level["levelId"],
            "LevelUniqueId": level["levelUniqueId"],
            "ModelId": level["modelId"],
        }
        for level in sorted_levels
    ]

    with target_path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=[
                "Model",
                "Type",
                "Level",
                "Elevation_ft",
                "Elevation_ft_in",
                "LevelId",
                "LevelUniqueId",
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
        "level_report generated.",
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

    levels = payload.get("levels", [])
    if not isinstance(levels, list):
        raise ValueError("levels must be a list.")

    validated_levels: List[Dict[str, Any]] = []

    for index, entry in enumerate(levels):
        if not isinstance(entry, dict):
            raise ValueError(f"levels[{index}] must be an object.")

        missing = REQUIRED_LEVEL_KEYS - entry.keys()
        if missing:
            raise ValueError(f"levels[{index}] missing keys: {sorted(missing)}")

        model = _to_str(entry["model"])
        model_id = _to_str(entry["modelId"])
        level_name = _to_str(entry["level"])
        level_type = _to_str(entry["type"])
        unique_id = _to_str(entry["levelUniqueId"])

        try:
            level_id = int(entry["levelId"])
        except (TypeError, ValueError) as exc:
            raise ValueError(f"levels[{index}].levelId must be an integer.") from exc

        try:
            elevation = float(entry["elevationFt"])
        except (TypeError, ValueError) as exc:
            raise ValueError(f"levels[{index}].elevationFt must be a number.") from exc

        validated_levels.append(
            {
                "model": model,
                "modelId": model_id,
                "type": level_type,
                "level": level_name,
                "elevationFt": elevation,
                "levelId": level_id,
                "levelUniqueId": unique_id,
            }
        )

    return {
        "schemaVersion": str(payload.get("schemaVersion", "1.0.0")),
        "includeLinkedModels": include_linked,
        "precision": precision,
        "maxPreviewRows": preview_rows,
        "targetPath": target_path.strip(),
        "levels": validated_levels,
    }


def _to_str(value: Any) -> str:
    text = "" if value is None else str(value)
    if not text.strip():
        raise ValueError("String value cannot be empty.")
    return text.strip()


def _format_feet_inches(feet_value: float, precision: int) -> str:
    total_inches = abs(feet_value) * 12
    feet = int(total_inches // 12)
    inches = total_inches - (feet * 12)
    rounded_inches = round(inches, precision)

    if rounded_inches >= 12:
        feet += 1
        rounded_inches = 0.0

    if precision == 0:
        inches_text = f"{int(round(rounded_inches))}"
    else:
        inches_text = f"{rounded_inches:.{precision}f}"

    sign = "-" if feet_value < 0 else ""
    return f"{sign}{feet}'-{inches_text}\""
