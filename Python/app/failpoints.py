from __future__ import annotations

import os
import threading
from typing import Callable

from app import structured_logging

TEST_PROFILE_VARIABLE = "COURSE_TEST_PROFILE"
FAILPOINT_VARIABLE = "COURSE_FAILPOINT"


class FailpointController:
    """Single armed commit-boundary failpoint, honoured in the test profile only.

    Reaching the boundary publishes exactly one acknowledgement line and then
    blocks the process. Waiting is a barrier, not a sleep: the harness stops the
    container after the acknowledgement, and the durable state that must survive
    is whatever the boundary already committed.
    """

    def __init__(
        self,
        armed: str | None,
        test_profile: bool,
        sleep: Callable[[float], None] = threading.Event().wait,
    ) -> None:
        self._armed = armed.strip() if armed else None
        self._enabled = test_profile and bool(self._armed)
        self._sleep = sleep
        self._reached: set[str] = set()
        self._lock = threading.Lock()

    @classmethod
    def from_environment(cls) -> "FailpointController":
        return cls(
            os.environ.get(FAILPOINT_VARIABLE),
            os.environ.get(TEST_PROFILE_VARIABLE) == "1",
        )

    @property
    def armed_name(self) -> str | None:
        return self._armed if self._enabled else None

    def is_armed(self, name: str) -> bool:
        return self._enabled and self._armed == name

    def reach(self, name: str, instance_id: str) -> None:
        if not self.is_armed(name):
            return
        with self._lock:
            first = name not in self._reached
            self._reached.add(name)
        if not first:
            return
        structured_logging.log(
            "failpoint.reached",
            name=name,
            instanceId=str(instance_id),
        )
        self._sleep(float("inf"))

    def describe(self) -> str:
        if not self._armed:
            return "none"
        if not self._enabled:
            return f"{self._armed}:ignored"
        return f"{self._armed}:armed"
