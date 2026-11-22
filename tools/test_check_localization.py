"""Tests for the localization resource completeness check."""

from pathlib import Path
import tempfile
import unittest

from check_localization import DEFAULT_ROOT, check


def resx(**entries):
    data = "".join(
        f'<data name="{name}" xml:space="preserve"><value>{value}</value></data>'
        for name, value in entries.items())
    return f'<?xml version="1.0" encoding="utf-8"?><root>{data}</root>'


class CheckLocalizationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.addCleanup(self.temporary.cleanup)

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def errors(self):
        return check(self.root, required_cultures=())[0]

    def test_complete_translation_passes(self):
        self.write("App/Strings.resx", resx(Title="Inbox", Count="{0} of {1:N0}"))
        self.write("App/Strings.cs.resx", resx(Title="Doručené", Count="{0} z {1:N0}"))
        errors, summary = check(self.root, required_cultures=())
        self.assertEqual([], errors)
        self.assertIn("cs, en", summary)

    def test_english_only_set_passes(self):
        self.write("Core/Shared.resx", resx(Title="Inbox"))
        self.assertEqual([], self.errors())

    def test_supported_languages_are_required_even_if_fully_missing(self):
        self.write("Strings.resx", resx(Title="Inbox"))
        errors = check(self.root)[0]
        for culture in ("cs", "de", "uk"):
            self.assertTrue(any(
                f"missing translation for culture '{culture}'" in error for error in errors))

    def test_culture_required_in_every_set(self):
        self.write("Core/Shared.resx", resx(Title="Inbox"))
        self.write("App/Strings.resx", resx(Title="Inbox"))
        self.write("App/Strings.cs.resx", resx(Title="Doručené"))
        errors = self.errors()
        self.assertEqual(1, len(errors))
        self.assertIn(str(self.root / "Core/Shared.cs.resx"), errors[0])
        self.assertIn("missing translation for culture 'cs'", errors[0])

    def test_deleting_entire_translation_fails(self):
        for folder in ("Core", "App"):
            self.write(f"{folder}/Strings.resx", resx(Title="Inbox"))
            self.write(f"{folder}/Strings.cs.resx", resx(Title="Doručené"))
        self.assertEqual([], self.errors())
        (self.root / "Core/Strings.cs.resx").unlink()
        self.assertTrue(any("missing translation for culture 'cs'" in e for e in self.errors()))

    def test_culture_union_includes_regional_cultures(self):
        for folder in ("Core", "App"):
            self.write(f"{folder}/Strings.resx", resx(Title="Inbox"))
        self.write("Core/Strings.cs.resx", resx(Title="Doručené"))
        self.write("App/Strings.de-DE.resx", resx(Title="Posteingang"))
        errors = self.errors()
        self.assertEqual(2, len(errors))
        self.assertTrue(any("missing translation for culture 'cs'" in e for e in errors))
        self.assertTrue(any("missing translation for culture 'de-DE'" in e for e in errors))
        self.write("App/Strings.cs.resx", resx(Title="Doručené"))
        self.write("Core/Strings.de-DE.resx", resx(Title="Posteingang"))
        self.assertEqual([], self.errors())

    def test_missing_and_extra_keys_fail(self):
        self.write("Strings.resx", resx(Title="Inbox", Sent="Sent"))
        self.write("Strings.de-DE.resx", resx(Title="Posteingang", Other="Andere"))
        errors = "\n".join(self.errors())
        self.assertIn("missing 'Sent'", errors)
        self.assertIn("'Other' is not in Strings.resx", errors)

    def test_empty_value_fails_in_any_language(self):
        self.write("Strings.resx", resx(Title=" "))
        self.write("Strings.uk.resx", resx(Title=""))
        self.assertEqual(2, sum("'Title' has an empty value" in e for e in self.errors()))

    def test_duplicate_key_fails(self):
        entry = '<data name="Title"><value>Inbox</value></data>'
        self.write("Strings.resx", f"<root>{entry}{entry}</root>")
        self.assertTrue(any("'Title' is defined 2 times" in e for e in self.errors()))

    def test_placeholder_mismatch_fails(self):
        self.write("Strings.resx", resx(A="{0}: {1}", B="{0:0} s", C="{{literal}} {0}"))
        self.write("Strings.cs.resx", resx(A="{0}", B="{0} s", C="{0} {{literal}}"))
        errors = self.errors()
        self.assertTrue(any("'A' placeholders differ" in e for e in errors))
        self.assertTrue(any("'B' placeholders differ" in e for e in errors))
        self.assertFalse(any("'C'" in e for e in errors))

    def test_translation_without_neutral_file_fails(self):
        self.write("Strings.resx", resx(Title="Inbox"))
        self.write("Other.cs.resx", resx(Title="Doručené"))
        self.assertTrue(any("has no neutral resource file" in e for e in self.errors()))

    def test_build_output_is_ignored(self):
        self.write("Strings.resx", resx(Title="Inbox"))
        self.write("obj/Strings.cs.resx", resx())
        self.assertEqual([], self.errors())

    def test_no_resources_fails(self):
        self.assertTrue(self.errors())

    def test_repository_resources_pass(self):
        errors, summary = check(DEFAULT_ROOT)
        self.assertEqual([], errors)
        self.assertIn("3 resource sets", summary)
        self.assertIn("cs, de, en, uk", summary)


if __name__ == "__main__":
    unittest.main()
