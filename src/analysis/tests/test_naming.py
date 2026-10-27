from photolib.naming import parse_capture_datetime, dup_group_key


def test_parse_pxl_filename():
    assert parse_capture_datetime("PXL_20260115_090000123.jpg") == "2026-01-15T09:00:00"


def test_parse_without_prefix():
    assert parse_capture_datetime("20260115_090000.jpg") == "2026-01-15T09:00:00"


def test_parse_edited_copy_suffix():
    assert parse_capture_datetime("PXL_20260115_090000123~2.jpg") == "2026-01-15T09:00:00"


def test_parse_no_date_returns_none():
    assert parse_capture_datetime("IMG_random.jpg") is None


def test_parse_invalid_date_returns_none():
    assert parse_capture_datetime("PXL_20261399_090000.jpg") is None


def test_dup_group_strips_tilde_suffix():
    assert dup_group_key("PXL_20260115_090000123~2.jpg") == "PXL_20260115_090000123"


def test_dup_group_strips_paren_suffix():
    assert dup_group_key("PXL_20260115_090000123 (1).jpg") == "PXL_20260115_090000123"


def test_dup_group_plain_name():
    assert dup_group_key("PXL_20260115_090000123.jpg") == "PXL_20260115_090000123"
