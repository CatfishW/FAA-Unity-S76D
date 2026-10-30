"""Private read-only snapshot transport; caller owns controls and safe pause.

Warm identifiers while paused, then reuse one bounded pool for independent GETs.
An interrupted/failed capture cancels queued reads without waiting for workers.
The controller must request/verify pause before calling close(), which joins them.
"""
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
import math
import threading


NATIVE_CLOCK = 'sim/time/total_flight_time_sec'
WORKERS = 8


class SnapshotError(RuntimeError):
    pass


def _number(value, label):
    if type(value) not in (int, float) or not math.isfinite(value):
        raise SnapshotError('Invalid snapshot ' + label)
    return value


class ParallelSnapshot:
    """Persistent independent-read pool, with serialized native clock brackets.

    No pause, command, write, or control retry occurs here. Call start() only after
    paused identity/ownership admission. Fake APIs may omit identifier(); the real
    NativeApi warms every identifier on the calling thread before creating a pool.
    A failed capture cannot be retried on the same instance.
    """
    def __init__(self, api, names, prop, clock):
        self.api, self.clock = api, clock
        self.names = tuple(dict.fromkeys(names))
        if any(name in ('mainCollective', 'tailPropRatio') for name in self.names):
            raise SnapshotError('Snapshot names conflict with indexed aliases')
        self.specs = tuple((name, name, None) for name in self.names) + (
            ('mainCollective', prop, 0), ('tailPropRatio', prop, 1))
        self.pool = None
        self.closed = self.failed = False
        self._capturing = False
        self._lock = threading.Lock()
        self._pending = set()

    def start(self):
        """Serial identifier warm-up, then exactly one persistent eight-worker pool."""
        with self._lock:
            if self.closed or self.failed:
                raise SnapshotError('Snapshot transport is closed or failed')
            if self.pool is not None:
                return self
            identifier = getattr(self.api, 'identifier', None)
            if identifier is not None:
                for name in dict.fromkeys((NATIVE_CLOCK,) + tuple(spec[1] for spec in self.specs)):
                    identifier(name)
            self.pool = ThreadPoolExecutor(max_workers=WORKERS, thread_name_prefix='faa-snapshot')
        return self

    def _read(self, spec):
        key, name, index = spec
        started_utc, started_mono = self.clock.time(), self.clock.monotonic()
        value = self.api.read(name) if index is None else self.api.read_index(name, index)
        completed_utc, completed_mono = self.clock.time(), self.clock.monotonic()
        if completed_mono < started_mono:
            raise SnapshotError('Snapshot field monotonic clock regressed')
        return key, value, {'name': name, 'index': index, 'startedUtc': started_utc,
            'completedUtc': completed_utc, 'startedMonotonic': started_mono,
            'completedMonotonic': completed_mono, 'readSeconds': completed_mono-started_mono}

    def capture(self, check_deadline=None):
        """Return deterministic values and measured observation timing.

        Native before/after reads are on the calling thread and surround all pool
        reads. On any failure (including a deadline/signal), remaining futures are
        cancelled but never joined here: the caller must pause before close().
        """
        check_deadline = check_deadline or (lambda: None)
        with self._lock:
            if self.pool is None or self.closed or self.failed:
                raise SnapshotError('Snapshot transport is not active')
            if self._capturing:
                raise SnapshotError('Overlapping snapshot capture is forbidden')
            self._capturing = True
        futures = set()
        try:
            check_deadline()
            started_utc, started_mono = self.clock.time(), self.clock.monotonic()
            before = _number(self.api.read(NATIVE_CLOCK), 'native clock before')
            check_deadline()
            futures = {self.pool.submit(self._read, spec) for spec in self.specs}
            with self._lock:
                self._pending = set(futures)
            pending, found = set(futures), {}
            while pending:
                check_deadline()
                done, pending = wait(pending, timeout=.025, return_when=FIRST_COMPLETED)
                for future in done:
                    key, value, timing = future.result()
                    found[key] = value, timing
            check_deadline()
            after = _number(self.api.read(NATIVE_CLOCK), 'native clock after')
            completed_utc, completed_mono = self.clock.time(), self.clock.monotonic()
            if after < before or completed_mono < started_mono:
                raise SnapshotError('Snapshot native or monotonic clock regressed')
            check_deadline()
            values = {key: found[key][0] for key, _, _ in self.specs}
            timings = {}
            for key, _, _ in self.specs:
                timing = found[key][1]
                timing['ageAtCompletionSeconds'] = completed_mono-timing['completedMonotonic']
                timing['oldestObservationAgeSeconds'] = completed_mono-timing['startedMonotonic']
                timings[key] = timing
            info = {'startedUtc': started_utc, 'completedUtc': completed_utc,
                'nativeClockBefore': before, 'nativeClockAfter': after,
                'captureWidthSeconds': completed_mono-started_mono,
                'nativeSpanSeconds': after-before, 'perFieldReadTimes': timings,
                'readWorkerLimit': WORKERS, 'independentReadCount': len(self.specs)}
            return values, info
        except BaseException:
            with self._lock:
                self.failed = True
            for future in futures:
                future.cancel()
            raise
        finally:
            with self._lock:
                self._capturing = False
                # Retain unresolved futures until close(); never wait in capture.
                self._pending = {future for future in futures if not future.done()}

    def close(self):
        """Cancel queued reads and join running workers; caller must pause first."""
        with self._lock:
            if self.closed:
                return
            self.closed = True
            pool, pending = self.pool, tuple(self._pending)
        for future in pending:
            future.cancel()
        if pool is not None:
            pool.shutdown(wait=True, cancel_futures=True)
        with self._lock:
            self._pending.clear()
