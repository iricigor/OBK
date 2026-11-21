"""Offline tests for the maintainer-only school directory refresh."""

import copy
from datetime import datetime, timezone
import gzip
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import quote

from refresh_school_directory import (
    DirectoryError, MAX_RESPONSE_BYTES, SOURCE, collect_directory, refresh, request_json,
)


TOWN = "Ho\u0159ice (okr. Ji\u010d\u00edn)"
SCHOOL = {
    "id": "school-1",
    "name": "Example school",
    "schoolUrl": "https://school.example:444/bakaweb/",
}


class FakeDirectory:
    def __init__(self):
        self.towns = [
            {"name": TOWN, "schoolCount": 1},
            {"name": "", "schoolCount": 16},
        ]
        self.schools = {"name": TOWN, "schools": [copy.deepcopy(SCHOOL)]}
        self.calls = []

    def __call__(self, url):
        self.calls.append(url)
        if url == SOURCE:
            return copy.deepcopy(self.towns)
        if url == SOURCE + "/" + quote(TOWN, safe=""):
            return copy.deepcopy(self.schools)
        raise AssertionError("Unexpected endpoint")


class DirectoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = FakeDirectory()
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.output = Path(self.temp.name) / "schools.json.gz"
        self.timestamp = datetime(2026, 11, 21, tzinfo=timezone.utc)

    def save(self):
        return refresh(self.output, self.directory, self.timestamp)

    def test_snapshot_roundtrip_exact_url_and_explicit_exclusion(self):
        self.assertTrue(self.save())
        data = self.output.read_bytes()
        self.assertEqual(data[4:8], b"\0\0\0\0")
        self.assertNotIn(SCHOOL["name"].encode(), data)
        result = json.loads(gzip.decompress(data))
        self.assertEqual(result["source"], SOURCE)
        self.assertEqual(result["schoolCount"], 1)
        self.assertEqual(result["municipalityCount"], 1)
        self.assertEqual(result["fetchedAt"], self.timestamp.isoformat(timespec="seconds"))
        self.assertEqual(result["schools"], [{
            **SCHOOL, "urlValid": True, "municipalityMatches": [TOWN],
        }])
        self.assertFalse(result["complete"])
        self.assertEqual(result["excludedMunicipalities"][0]["schoolCount"], 16)
        self.assertEqual(
            self.directory.calls, [SOURCE, SOURCE + "/" + quote(TOWN, safe=""), SOURCE]
        )

    def test_unchanged_refresh_preserves_bytes_and_timestamp(self):
        self.save()
        original = self.output.read_bytes()
        self.directory.towns.reverse()
        self.assertFalse(refresh(
            self.output, self.directory, datetime(2026, 12, 1, tzinfo=timezone.utc)
        ))
        self.assertEqual(self.output.read_bytes(), original)

    def test_changed_school_replaces_snapshot(self):
        self.save()
        self.directory.schools["schools"][0]["schoolUrl"] = "http://new.example/baka/"
        self.assertTrue(self.save())
        result = json.loads(gzip.decompress(self.output.read_bytes()))
        self.assertEqual(
            result["schools"][0]["schoolUrl"], "http://new.example/baka/"
        )

    def test_no_usable_schools_never_replaces_snapshot(self):
        self.save()
        original = self.output.read_bytes()
        for invalid in (
            {"name": TOWN, "schools": []},
            {"name": "Wrong town", "schools": [SCHOOL]},
            {"name": TOWN, "schools": [{**SCHOOL, "name": ""}]},
        ):
            with self.subTest(invalid=invalid):
                self.directory.schools = invalid
                with self.assertRaises(DirectoryError):
                    self.save()
                self.assertEqual(self.output.read_bytes(), original)

    def test_http_failure_preserves_previous_snapshot(self):
        self.save()
        original = self.output.read_bytes()

        def failed(url):
            if url != SOURCE:
                error = HTTPError(url, 500, "Server failure", {}, None)
                self.addCleanup(error.close)
                raise error
            return self.directory(url)

        with self.assertRaises(DirectoryError):
            refresh(self.output, failed)
        self.assertEqual(self.output.read_bytes(), original)

    def test_list_changed_during_collection_is_reported(self):
        calls = 0

        def changed(url):
            nonlocal calls
            result = self.directory(url)
            if url == SOURCE:
                calls += 1
                if calls == 2:
                    result[0]["schoolCount"] += 1
            return result

        result = collect_directory(changed)
        self.assertEqual(result["schoolCount"], 1)
        self.assertIn("changed during collection", result["diagnostics"][0]["error"])
        self.assertFalse(result["complete"])

    def test_empty_duplicate_or_invalid_municipalities_fail(self):
        for towns in (
            [], [{"name": "", "schoolCount": 16}], self.directory.towns * 2,
            [{"name": TOWN, "schoolCount": True}],
        ):
            with self.subTest(towns=towns):
                self.directory.towns = towns
                with self.assertRaises(DirectoryError):
                    collect_directory(self.directory)

    def test_duplicate_search_results_are_deduplicated_with_lookup_associations(self):
        self.directory.towns[0]["schoolCount"] = 2
        self.directory.schools["schools"] *= 2
        result = collect_directory(self.directory)
        self.assertEqual(result["schoolCount"], 1)
        self.assertEqual(result["schools"][0]["municipalityMatches"], [TOWN])
        self.assertIn("Expected 2 unique", result["diagnostics"][0]["error"])

    def test_partial_collection_skips_failures_and_bad_records(self):
        self.directory.towns.append({"name": "Unavailable town", "schoolCount": 1})
        self.directory.schools["schools"].append({"id": "broken"})

        def partial(url):
            if url.endswith("/Unavailable%20town"):
                raise OSError("Connection failed")
            return self.directory(url)

        result = collect_directory(partial)
        self.assertEqual(result["schoolCount"], 1)
        self.assertEqual(result["expectedSchoolCount"], 2)
        self.assertTrue(any("Connection failed" in item["error"] for item in result["diagnostics"]))
        self.assertTrue(any("malformed school record" in item["error"] for item in result["diagnostics"]))
        self.assertFalse(result["municipalities"][1]["retrieved"])
        self.assertFalse(result["complete"])

    def test_upstream_url_anomalies_are_retained_and_flagged(self):
        for url in ("example.cz", "https:/example.cz", "", "https://user:pass@example"):
            with self.subTest(url=url):
                self.directory.schools["schools"][0]["schoolUrl"] = url
                result = collect_directory(self.directory)
                self.assertEqual(result["schools"][0]["schoolUrl"], url)
                self.assertFalse(result["schools"][0]["urlValid"])
                self.assertEqual(result["invalidUrlCount"], 1)

    def test_neighboring_town_matches_and_conflicts_preserve_one_record(self):
        self.directory.towns.append({"name": "Neighbor", "schoolCount": 0})

        def overlapping(url):
            if url.endswith("/Neighbor"):
                return {"name": "Neighbor", "schools": [{**SCHOOL, "name": "Conflicting name"}]}
            return self.directory(url)

        result = collect_directory(overlapping)
        self.assertEqual(result["schoolCount"], 1)
        self.assertEqual(result["schools"][0]["name"], SCHOOL["name"])
        self.assertEqual(result["schools"][0]["municipalityMatches"], [TOWN, "Neighbor"])
        self.assertIn("Conflicting duplicate", result["diagnostics"][0]["error"])

    def test_failed_final_index_check_keeps_usable_records(self):
        calls = 0

        def failed_recheck(url):
            nonlocal calls
            if url == SOURCE:
                calls += 1
                if calls == 2:
                    raise OSError("Index unavailable")
            return self.directory(url)

        result = collect_directory(failed_recheck)
        self.assertEqual(result["schoolCount"], 1)
        self.assertIn("Could not recheck", result["diagnostics"][0]["error"])

    def test_corrupt_existing_snapshot_is_not_replaced(self):
        self.output.write_bytes(b"not gzip")
        with self.assertRaises(DirectoryError):
            self.save()
        self.assertEqual(self.output.read_bytes(), b"not gzip")

    def test_failed_atomic_replace_preserves_snapshot_and_removes_temp(self):
        self.save()
        original = self.output.read_bytes()
        self.directory.schools["schools"][0]["name"] = "Changed school"
        with patch("refresh_school_directory.os.replace", side_effect=OSError("Write failed")):
            with self.assertRaises(OSError):
                self.save()
        self.assertEqual(self.output.read_bytes(), original)
        self.assertEqual(list(self.output.parent.iterdir()), [self.output])

    def test_network_request_json_header_size_limit_and_invalid_json(self):
        with patch("refresh_school_directory.build_opener") as factory:
            response = factory.return_value.open.return_value.__enter__.return_value
            for content in (b"broken", b"x" * (MAX_RESPONSE_BYTES + 1)):
                response.read.return_value = content
                with self.assertRaises(DirectoryError):
                    request_json(SOURCE)
            response.read.return_value = b'{"schools":[]}'
            self.assertEqual(request_json(SOURCE), {"schools": []})
            request = factory.return_value.open.call_args.args[0]
            self.assertEqual(request.full_url, SOURCE)
            self.assertEqual(request.get_header("Accept"), "application/json")
            self.assertEqual(factory.return_value.open.call_args.kwargs["timeout"], 30)


if __name__ == "__main__":
    unittest.main()
