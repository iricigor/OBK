"""Refresh the unused, compressed public school directory without credentials."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import gzip
from http.client import HTTPException
import json
import os
from pathlib import Path
import sys
import tempfile
from urllib.error import HTTPError
from urllib.parse import quote, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


SOURCE = "https://sluzby.bakalari.cz/api/v1/municipality"
DEFAULT_OUTPUT = Path(__file__).resolve().parent.parent / "data" / "schools.json.gz"
MAX_RESPONSE_BYTES = 4 * 1024 * 1024


class DirectoryError(Exception):
    """A failed refresh that must not replace the previous snapshot."""


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request_json(url):
    request = Request(url, headers={"Accept": "application/json"})
    try:
        with build_opener(NoRedirects()).open(request, timeout=30) as response:
            content = response.read(MAX_RESPONSE_BYTES + 1)
    except HTTPError as error:
        error.close()
        raise
    if len(content) > MAX_RESPONSE_BYTES:
        raise DirectoryError("Directory response exceeded the 4 MB limit.")
    try:
        return json.loads(content)
    except (ValueError, UnicodeError) as error:
        raise DirectoryError("Directory returned invalid JSON.") from error


def municipalities(value):
    if not isinstance(value, list) or not value:
        raise DirectoryError("Directory returned no valid municipality list.")
    result = []
    seen = set()
    for item in value:
        if (
            not isinstance(item, dict)
            or not isinstance(item.get("name"), str)
            or type(item.get("schoolCount")) is not int
            or item["schoolCount"] < 0
            or item["name"] in seen
            or (item["name"] and not item["name"].strip())
        ):
            raise DirectoryError("Directory returned invalid or duplicate municipalities.")
        seen.add(item["name"])
        result.append({"name": item["name"], "schoolCount": item["schoolCount"]})
    if not any(item["name"] and item["schoolCount"] for item in result):
        raise DirectoryError("Directory contains no retrievable schools.")
    return sorted(result, key=lambda item: item["name"])


def valid_school_url(value):
    try:
        parts = urlsplit(value)
        port = parts.port
    except ValueError:
        return False
    return (
        parts.scheme in ("http", "https")
        and bool(parts.hostname)
        and parts.username is None
        and parts.password is None
        and (port is None or 1 <= port <= 65535)
        and "\\" not in value
        and not any(character.isspace() or ord(character) < 32 for character in value)
    )


def fetch_municipality(item, fetch):
    name = item["name"]
    try:
        value = fetch(SOURCE + "/" + quote(name, safe=""))
        if (
            not isinstance(value, dict)
            or value.get("name") != name
            or not isinstance(value.get("schools"), list)
        ):
            raise DirectoryError("Invalid municipality response.")
    except (DirectoryError, HTTPException, OSError, ValueError) as error:
        return item, [], [{"municipality": name, "error": str(error)}], False
    schools = []
    diagnostics = []
    for index, school in enumerate(value["schools"]):
        if (
            not isinstance(school, dict)
            or any(
                not isinstance(school.get(key), str) or not school[key].strip()
                for key in ("id", "name")
            )
            or not isinstance(school.get("schoolUrl"), str)
        ):
            diagnostics.append({
                "municipality": name, "recordIndex": index,
                "error": "Skipped a malformed school record.",
            })
            continue
        schools.append({
            "id": school["id"],
            "name": school["name"],
            "schoolUrl": school["schoolUrl"],
            "urlValid": valid_school_url(school["schoolUrl"]),
        })
    return item, schools, diagnostics, True


def collect_directory(fetch=request_json):
    before = municipalities(fetch(SOURCE))
    named = [item for item in before if item["name"]]
    # The API advertises an unnamed group but cannot retrieve it. Keep this
    # agreed exclusion explicit; never apply it to failures for named towns.
    excluded = [
        {**item, "reason": "The unnamed municipality cannot be retrieved from the API."}
        for item in before if not item["name"]
    ]
    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(lambda item: fetch_municipality(item, fetch), named))
    diagnostics = []
    schools = {}
    towns = []
    for item, records, errors, succeeded in results:
        diagnostics.extend(errors)
        towns.append({
            **item, "retrieved": succeeded, "returnedSchoolCount": len(records),
        })
        for record in sorted(records, key=lambda school: (school["id"], school["name"], school["schoolUrl"])):
            identifier = record["id"]
            if identifier not in schools:
                schools[identifier] = {**record, "municipalityMatches": []}
            previous = schools[identifier]
            if any(previous[key] != record[key] for key in ("name", "schoolUrl")):
                diagnostics.append({
                    "municipality": item["name"], "schoolId": identifier,
                    "error": "Conflicting duplicate; retained the first record in municipality order.",
                })
            if item["name"] not in previous["municipalityMatches"]:
                previous["municipalityMatches"].append(item["name"])
    if not schools:
        raise DirectoryError("No usable schools were retrieved; the previous snapshot is preserved.")
    try:
        if municipalities(fetch(SOURCE)) != before:
            diagnostics.append({"error": "Municipality index changed during collection."})
    except (DirectoryError, HTTPException, OSError, ValueError) as error:
        diagnostics.append({"error": f"Could not recheck municipality index: {error}"})
    expected = sum(item["schoolCount"] for item in named)
    if len(schools) != expected:
        diagnostics.append({
            "error": f"Expected {expected} unique named-group schools; collected {len(schools)}.",
        })
    invalid_urls = sum(not school["urlValid"] for school in schools.values())
    for diagnostic in diagnostics:
        print("WARNING: " + json.dumps(diagnostic, ensure_ascii=True), file=sys.stderr)
    if excluded:
        print(
            f"WARNING: Unnamed-group schools excluded: {sum(item['schoolCount'] for item in excluded)}.",
            file=sys.stderr,
        )
    if invalid_urls:
        print(f"WARNING: Preserved {invalid_urls} malformed upstream URLs.", file=sys.stderr)
    return {
        "formatVersion": 1,
        "source": SOURCE,
        "municipalityCount": len(towns),
        "expectedSchoolCount": expected,
        "schoolCount": len(schools),
        "invalidUrlCount": invalid_urls,
        "complete": not diagnostics and not excluded and not invalid_urls,
        "excludedMunicipalities": excluded,
        "diagnostics": diagnostics,
        "municipalities": towns,
        "schools": [schools[identifier] for identifier in sorted(schools)],
    }


def refresh(output=DEFAULT_OUTPUT, fetch=request_json, now=None):
    payload = collect_directory(fetch)
    if output.exists():
        try:
            with gzip.open(output, "rt", encoding="utf-8") as stream:
                previous = json.load(stream)
        except (OSError, EOFError, ValueError, UnicodeError) as error:
            raise DirectoryError("Existing snapshot is unreadable; restore it before refreshing.") from error
        if not isinstance(previous, dict) or not isinstance(previous.get("fetchedAt"), str):
            raise DirectoryError("Existing snapshot has invalid metadata.")
        previous.pop("fetchedAt")
        if previous == payload:
            print("Directory unchanged; snapshot and retrieval timestamp preserved.")
            return False
    timestamp = now if now is not None else datetime.now(timezone.utc)
    payload["fetchedAt"] = timestamp.astimezone(timezone.utc).isoformat(timespec="seconds")
    content = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=output.parent, delete=False) as stream:
            temporary = Path(stream.name)
            # Empty filename and fixed timestamp avoid machine/time-specific headers.
            with gzip.GzipFile(filename="", mode="wb", fileobj=stream, mtime=0) as compressed:
                compressed.write(content.encode("utf-8"))
        os.replace(temporary, output)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()
    excluded_count = sum(item["schoolCount"] for item in payload["excludedMunicipalities"])
    print(
        f"Saved {payload['schoolCount']} schools across {payload['municipalityCount']} "
        f"municipality lookups. Excluded unnamed-group schools: {excluded_count}. "
        f"Diagnostics: {len(payload['diagnostics'])}; malformed URLs: {payload['invalidUrlCount']}."
    )
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    arguments = parser.parse_args()
    try:
        refresh(arguments.output)
    except (DirectoryError, HTTPException, OSError, ValueError) as error:
        print(f"School directory refresh failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
