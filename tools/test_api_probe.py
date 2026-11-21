import io
import json
import unittest
from urllib.error import HTTPError
from urllib.parse import parse_qs

from api_probe import ApiProbe, MAX_RESPONSE_BYTES, NoRedirects, ProbeError, normalize_url


class FakeOpener:
    def __init__(self, responses):
        self.responses = iter(responses)
        self.requests = []

    def open(self, request, timeout):
        self.requests.append(request)
        value = next(self.responses)
        if isinstance(value, Exception):
            raise value
        return io.BytesIO(value if isinstance(value, bytes) else json.dumps(value).encode())


class ApiProbeTests(unittest.TestCase):
    def test_authentication_and_read_only_message_request(self):
        opener = FakeOpener(
            [
                {"ApiVersion": "3.52.1"},
                {"access_token": "test-token", "expires_in": 3600},
                {"Messages": [{"Attachments": [{"Id": "a"}]}, {"Attachments": []}]},
            ]
        )
        result = ApiProbe("https://school.example/", opener).run("user", "p&= word")
        self.assertEqual((result.messages_on_page, result.attachments_on_page), (2, 1))
        self.assertEqual(result.expires_in, 3600)
        self.assertEqual(
            [request.full_url for request in opener.requests],
            [
                "https://school.example/api/3",
                "https://school.example/api/login",
                "https://school.example/api/3/komens/messages/received",
            ],
        )
        self.assertEqual(
            [request.get_method() for request in opener.requests], ["GET", "POST", "POST"]
        )
        login = opener.requests[1]
        self.assertEqual(parse_qs(login.data.decode())["password"], ["p&= word"])
        self.assertIsNone(login.get_header("Authorization"))
        self.assertEqual(
            opener.requests[2].get_header("Authorization"), "Bearer test-token"
        )
        self.assertEqual(
            json.loads(opener.requests[2].data),
            {"Pagination": {"PageNumber": 1, "PageSize": 10}},
        )
        self.assertNotIn("test-token", repr(result))

    def test_empty_inbox_is_success(self):
        opener = FakeOpener(
            [{"ApiVersion": "3"}, {"access_token": "token"}, {"Messages": []}]
        )
        result = ApiProbe("https://school.example", opener).run("u", "p")
        self.assertEqual(result.messages_on_page, 0)

    def test_rejects_unsafe_or_incorrect_base_urls(self):
        for url in (
            "http://school.example",
            "https://user:password@school.example",
            "https://school.example/?token=secret",
            "https://school.example/#fragment",
            "https://school.example/login",
            "https://school.example:bad",
            "https://school.example\\other",
            "https://school.example/\nheader",
        ):
            with self.subTest(url=url), self.assertRaises(ProbeError):
                normalize_url(url)
        self.assertEqual(
            normalize_url("https://school.example/baka/"),
            "https://school.example/baka",
        )

    def test_no_redirects_even_for_same_origin(self):
        request = object()
        self.assertIsNone(
            NoRedirects().redirect_request(
                request, None, 302, "Found", {}, "https://school.example/other"
            )
        )

    def test_login_failure_does_not_expose_response_or_send_more_requests(self):
        error = HTTPError(
            "https://school.example/api/login", 400, "secret-password", {},
            io.BytesIO(b'{"error_description":"secret-password"}'),
        )
        opener = FakeOpener([{"ApiVersion": "3"}, error])
        with self.assertRaises(ProbeError) as caught:
            ApiProbe("https://school.example", opener).run("u", "secret-password")
        self.assertNotIn("secret-password", str(caught.exception))
        self.assertEqual(len(opener.requests), 2)

    def test_redirect_is_reported_without_following(self):
        error = HTTPError("https://school.example/api/3", 302, "Found", {}, io.BytesIO())
        with self.assertRaisesRegex(ProbeError, "redirect"):
            ApiProbe("https://school.example", FakeOpener([error])).run("u", "p")

    def test_invalid_responses_are_not_success(self):
        for received in ({}, {"Messages": None}, {"Messages": [{}]}, b"<html>Login</html>"):
            with self.subTest(received=received), self.assertRaises(ProbeError):
                opener = FakeOpener(
                    [{"ApiVersion": "3"}, {"access_token": "token"}, received]
                )
                ApiProbe("https://school.example", opener).run("u", "p")

    def test_invalid_token_stops_before_komens(self):
        for token in (None, "", 42, "token\r\nInjected: header"):
            with self.subTest(token=token):
                opener = FakeOpener([{"ApiVersion": "3"}, {"access_token": token}])
                with self.assertRaises(ProbeError):
                    ApiProbe("https://school.example", opener).run("u", "p")
                self.assertEqual(len(opener.requests), 2)

    def test_response_size_limit(self):
        opener = FakeOpener([b" " * (MAX_RESPONSE_BYTES + 1)])
        with self.assertRaisesRegex(ProbeError, "2 MB"):
            ApiProbe("https://school.example", opener).run("u", "p")

    def test_empty_credentials_send_nothing(self):
        opener = FakeOpener([])
        with self.assertRaises(ProbeError):
            ApiProbe("https://school.example", opener).run("u", "")
        self.assertEqual(opener.requests, [])


if __name__ == "__main__":
    unittest.main()
