from __future__ import annotations

import threading

from app.failpoints import FailpointController


class _Recorder:
    def __init__(self) -> None:
        self.lines: list[str] = []
        self._lock = threading.Lock()

    def wait(self, seconds: float) -> None:  # noqa: ARG002 - stand-in for the blocking barrier
        with self._lock:
            self.lines.append("blocked")


def _controller(armed: str | None, test_profile: bool = True) -> tuple[FailpointController, _Recorder]:
    recorder = _Recorder()
    return FailpointController(armed, test_profile, sleep=recorder.wait), recorder


def test_disarmed_without_test_profile() -> None:
    controller, recorder = _controller("after_outbox_claim", test_profile=False)
    controller.reach("after_outbox_claim", "id-1")
    assert recorder.lines == []
    assert controller.armed_name is None
    assert controller.describe() == "after_outbox_claim:ignored"


def test_unnamed_failpoint_is_a_no_op() -> None:
    controller, recorder = _controller(None)
    controller.reach("after_outbox_claim", "id-1")
    assert recorder.lines == []
    assert controller.describe() == "none"


def test_only_the_armed_name_blocks() -> None:
    controller, recorder = _controller("after_outbox_claim")
    controller.reach("after_provider_response", "id-1")
    assert recorder.lines == []
    controller.reach("after_outbox_claim", "id-1")
    assert recorder.lines == ["blocked"]


def test_acknowledgement_is_published_once(capsys) -> None:
    controller, recorder = _controller("after_outbox_claim")
    controller.reach("after_outbox_claim", "outbox-1")
    controller.reach("after_outbox_claim", "outbox-2")
    assert recorder.lines == ["blocked"]
    captured = capsys.readouterr().out.strip().splitlines()
    assert len(captured) == 1
    assert '"event":"failpoint.reached"' in captured[0]
    assert '"name":"after_outbox_claim"' in captured[0]
    assert '"instanceId":"outbox-1"' in captured[0]


def test_blank_failpoint_is_not_armed() -> None:
    controller, recorder = _controller("   ")
    controller.reach("after_outbox_claim", "id-1")
    assert recorder.lines == []
    assert controller.describe() == "none"
