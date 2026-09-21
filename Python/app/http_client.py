from __future__ import annotations

import socket
import urllib.error
import urllib.request


class HttpError(RuntimeError):
    """Transport error, timeout or connection failure."""


def post_json(url: str, payload: bytes, headers: dict[str, str], timeout: float):
    request = urllib.request.Request(url, data=payload, method="POST", headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read(), dict(response.headers.items())
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers.items())
    except (urllib.error.URLError, socket.timeout, TimeoutError, OSError) as error:
        raise HttpError(str(error)) from error