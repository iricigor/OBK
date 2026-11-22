"""Verify that every translation of each .resx string resource set is complete.

Each resource set is a neutral English file such as ``AppStrings.resx`` plus any
translations named ``AppStrings.<culture>.resx`` in the same directory. Sets are
discovered automatically, so new languages are checked without configuration.
Every discovered language must have a translation in every neutral resource set.
"""

import argparse
from collections import Counter
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


DEFAULT_ROOT = Path(__file__).resolve().parent.parent / "src"
CULTURE = re.compile(r"^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$")
PLACEHOLDER = re.compile(r"(?<!\{)\{(\d+)(?:,\s*-?\d+)?(?::([^{}]*))?\}")
SKIPPED_DIRECTORIES = {"bin", "obj", ".git"}


def parse_resx(path):
    """Returns ({key: value}, errors) for the text entries of one .resx file."""
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        return {}, [f"invalid XML: {error}"]
    values, errors = {}, []
    names = Counter()
    for data in root.iter("data"):
        name = data.get("name")
        if not name:
            errors.append("an entry has no name")
            continue
        names[name] += 1
        if data.get("type") or data.get("mimetype"):
            errors.append(f"'{name}' is not a text resource")
            continue
        value = data.find("value")
        text = value.text if value is not None else None
        if text is None or not text.strip():
            errors.append(f"'{name}' has an empty value")
        values[name] = text or ""
    errors += [f"'{name}' is defined {count} times" for name, count in sorted(names.items()) if count > 1]
    return values, errors


def placeholders(value):
    """Composite-format placeholders as sorted (index, format) pairs, ignoring escaped braces."""
    return sorted((int(index), fmt or "") for index, fmt in PLACEHOLDER.findall(value.replace("{{", "")))


def discover(root):
    """Maps each neutral .resx path to its translations ({culture: path}); also returns orphans."""
    neutral, translations = {}, {}
    for path in sorted(root.rglob("*.resx")):
        if SKIPPED_DIRECTORIES.intersection(path.relative_to(root).parts[:-1]):
            continue
        stem, _, culture = path.stem.rpartition(".")
        if stem and CULTURE.match(culture):
            translations[path] = (path.with_name(stem + ".resx"), culture)
        else:
            neutral[path] = {}
    orphans = []
    for path, (base, culture) in translations.items():
        if base in neutral:
            neutral[base][culture] = path
        else:
            orphans.append(path)
    return neutral, orphans


def check(root):
    """Returns (errors, summary) for all resource sets below root."""
    root = Path(root)
    sets, orphans = discover(root)
    errors = [f"{path}: translation has no neutral resource file" for path in orphans]
    if not sets:
        errors.append(f"{root}: no .resx resource sets found")
    keys = 0
    required_cultures = {culture for cultures in sets.values() for culture in cultures}
    for base, cultures in sets.items():
        neutral, problems = parse_resx(base)
        keys += len(neutral)
        errors += [f"{base}: {problem}" for problem in problems]
        for culture in sorted(required_cultures - cultures.keys()):
            path = base.with_name(f"{base.stem}.{culture}.resx")
            errors.append(f"{path}: missing translation for culture '{culture}'")
        for culture, path in sorted(cultures.items()):
            translated, problems = parse_resx(path)
            errors += [f"{path}: {problem}" for problem in problems]
            errors += [f"{path}: missing '{key}'" for key in sorted(neutral.keys() - translated.keys())]
            errors += [f"{path}: '{key}' is not in {base.name}" for key in sorted(translated.keys() - neutral.keys())]
            for key in sorted(neutral.keys() & translated.keys()):
                if placeholders(neutral[key]) != placeholders(translated[key]):
                    errors.append(f"{path}: '{key}' placeholders differ from {base.name}")
    languages = sorted({"en"} | required_cultures)
    summary = f"Checked {len(sets)} resource sets ({keys} keys) for languages: {', '.join(languages)}."
    return errors, summary


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT, help="directory to search (default: src)")
    errors, summary = check(parser.parse_args(argv).root)
    for error in errors:
        print(f"error: {error}", file=sys.stderr)
    print(summary)
    if errors:
        print(f"{len(errors)} localization problem(s) found.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
