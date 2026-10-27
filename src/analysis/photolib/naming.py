"""Parse capture datetime and duplicate-group key from photo filenames."""

import re
from datetime import datetime

_DT = re.compile(r"(\d{8})[_-](\d{6})")
_SUFFIX = re.compile(r"(~\d+|\s*\(\d+\))$")


def parse_capture_datetime(filename):
    """Return ISO 8601 capture time parsed from the filename, or None."""
    match = _DT.search(filename)
    if not match:
        return None
    date, time = match.group(1), match.group(2)
    try:
        dt = datetime(
            int(date[0:4]), int(date[4:6]), int(date[6:8]),
            int(time[0:2]), int(time[2:4]), int(time[4:6]),
        )
    except ValueError:
        return None
    return dt.isoformat()


def dup_group_key(filename):
    """Return the base name shared by a photo and its edited copies."""
    stem = filename.rsplit(".", 1)[0]
    prev = None
    while prev != stem:
        prev = stem
        stem = _SUFFIX.sub("", stem).rstrip()
    return stem
