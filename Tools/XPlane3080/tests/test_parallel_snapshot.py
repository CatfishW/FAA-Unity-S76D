"""Real bounded read scheduling tests; no simulator or network operations."""
import copy
import threading
import time
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import parallel_snapshot as transport


PROP = 'test/prop'


class ReadOnlyApi:
    def __init__(self, count=24, delay=.003):
        self.values = {'field'+str(i): [i, i+.25] for i in range(count)}
        self.delay = delay
        self.active = self.maximum = 0
        self.lock = threading.Lock()
        self.events, self.identifiers, self.worker_threads = [], [], set()
        self.started = threading.Event()
        self.release = None
        self.fail = None

    def identifier(self, name):
        with self.lock:
            self.identifiers.append((name, threading.get_ident()))
        return len(self.identifiers)

    def read(self, name):
        if name == transport.NATIVE_CLOCK:
            with self.lock:
                self.events.append(('clock', threading.get_ident(), self.active))
            return 123.5
        return self._value(name)

    def read_index(self, name, index):
        if name != PROP:
            raise AssertionError('Unexpected indexed reference')
        return self._value('main' if index == 0 else 'tail')

    def _value(self, name):
        with self.lock:
            self.active += 1
            self.maximum = max(self.maximum, self.active)
            self.events.append(('begin', name))
            self.worker_threads.add(threading.current_thread())
        self.started.set()
        try:
            if name == self.fail:
                raise OSError('read failed: ' + name)
            if self.release is not None:
                if not self.release.wait(2):
                    raise TimeoutError('fake blocked read timed out')
            time.sleep(self.delay)
            return .51 if name == 'main' else .63 if name == 'tail' else copy.deepcopy(self.values[name])
        finally:
            with self.lock:
                self.active -= 1
                self.events.append(('end', name))


class ParallelSnapshotTests(unittest.TestCase):
    def make(self, api=None):
        api = ReadOnlyApi() if api is None else api
        helper = transport.ParallelSnapshot(api, tuple(api.values), PROP, time)
        self.addCleanup(helper.close)
        return api, helper

    def test_parallel_width_mapping_equivalence_and_native_brackets(self):
        api, helper = self.make()
        owner = threading.get_ident()
        helper.start()
        values, info = helper.capture()
        self.assertGreater(api.maximum, 1)
        self.assertLessEqual(api.maximum, 8)
        self.assertEqual(list(values), list(api.values)+['mainCollective', 'tailPropRatio'])
        self.assertEqual({k: values[k] for k in api.values}, api.values)
        self.assertEqual((values['mainCollective'], values['tailPropRatio']), (.51, .63))
        self.assertEqual(api.events[0], ('clock', owner, 0))
        self.assertEqual(api.events[-1], ('clock', owner, 0))
        self.assertEqual(info['nativeClockBefore'], info['nativeClockAfter'])
        self.assertEqual(info['independentReadCount'], len(api.values)+2)
        self.assertEqual(info['readWorkerLimit'], 8)

    def test_identifiers_warm_serially_before_any_worker_and_pool_is_persistent(self):
        api, helper = self.make()
        owner = threading.get_ident()
        helper.start()
        self.assertEqual(api.events, [])
        self.assertTrue(all(thread == owner for _, thread in api.identifiers))
        self.assertEqual([name for name, _ in api.identifiers],
            [transport.NATIVE_CLOCK]+list(api.values)+[PROP])
        first_pool = helper.pool
        first, _ = helper.capture()
        first_threads = set(api.worker_threads)
        second, _ = helper.capture()
        helper.start()
        self.assertIs(helper.pool, first_pool)
        self.assertEqual(first, second)
        self.assertEqual(api.worker_threads, first_threads)
        self.assertEqual(len(api.identifiers), len(api.values)+2)

    def test_per_field_timing_evidence_uses_actual_observation_times(self):
        api, helper = self.make()
        helper.start()
        _, info = helper.capture()
        self.assertGreater(info['captureWidthSeconds'], 0)
        self.assertEqual(info['nativeSpanSeconds'], 0)
        self.assertEqual(set(info['perFieldReadTimes']), set(api.values)|{'mainCollective', 'tailPropRatio'})
        for row in info['perFieldReadTimes'].values():
            self.assertLessEqual(row['startedUtc'], row['completedUtc'])
            self.assertGreaterEqual(row['readSeconds'], 0)
            self.assertGreaterEqual(row['ageAtCompletionSeconds'], 0)
            self.assertGreaterEqual(row['oldestObservationAgeSeconds'], row['ageAtCompletionSeconds'])
            self.assertLessEqual(row['oldestObservationAgeSeconds'], info['captureWidthSeconds'])

    def test_worker_error_returns_before_waiting_for_running_reads(self):
        api = ReadOnlyApi()
        api.release, api.fail = threading.Event(), 'field0'
        _, helper = self.make(api)
        self.addCleanup(api.release.set)
        helper.start()
        started = time.monotonic()
        with self.assertRaisesRegex(OSError, 'read failed'):
            helper.capture()
        self.assertLess(time.monotonic()-started, .5)
        self.assertTrue(helper.failed)
        self.assertGreater(api.active, 0)
        with self.assertRaisesRegex(transport.SnapshotError, 'not active'):
            helper.capture()
        # The controller's pause action belongs at this exact point, before join.
        api.events.append(('pause-owned-by-controller',))
        api.release.set()
        helper.close()
        self.assertEqual(api.active, 0)
        self.assertTrue(all(not thread.is_alive() for thread in api.worker_threads))

    def test_deadline_and_interrupt_do_not_wait_for_workers(self):
        for exception in (TimeoutError, KeyboardInterrupt):
            with self.subTest(exception=exception):
                api = ReadOnlyApi()
                api.release = threading.Event()
                _, helper = self.make(api)
                helper.start()
                def deadline():
                    if api.started.is_set():
                        raise exception('requested stop')
                started = time.monotonic()
                try:
                    with self.assertRaisesRegex(exception, 'requested stop'):
                        helper.capture(deadline)
                    self.assertLess(time.monotonic()-started, .5)
                    self.assertTrue(helper.failed)
                    self.assertGreater(api.active, 0)
                finally:
                    api.release.set()
                    helper.close()
                self.assertTrue(all(not thread.is_alive() for thread in api.worker_threads))

    def test_overlapping_capture_refuses_without_disrupting_first_capture(self):
        api = ReadOnlyApi()
        api.release = threading.Event()
        _, helper = self.make(api)
        helper.start()
        result = []
        def first():
            try:
                result.append(helper.capture())
            except BaseException as exc:
                result.append(exc)
        thread = threading.Thread(target=first)
        thread.start()
        self.assertTrue(api.started.wait(.5))
        try:
            with self.assertRaisesRegex(transport.SnapshotError, 'Overlapping'):
                helper.capture()
            self.assertFalse(helper.failed)
        finally:
            api.release.set()
            thread.join(1)
        self.assertFalse(thread.is_alive())
        self.assertEqual(len(result), 1)
        self.assertIsInstance(result[0], tuple)

    def test_warm_failure_creates_no_pool_and_no_read_control_operations(self):
        api, helper = self.make()
        def failed(name):
            raise OSError('identifier unavailable')
        api.identifier = failed
        with self.assertRaisesRegex(OSError, 'identifier unavailable'):
            helper.start()
        self.assertIsNone(helper.pool)
        self.assertEqual(api.events, [])
        helper.close()
        helper.close()
        with self.assertRaises(transport.SnapshotError):
            helper.start()

    def test_regressed_or_nonfinite_native_clock_refuses(self):
        for after in (123., float('nan')):
            with self.subTest(after=after):
                api, helper = self.make()
                read, clocks = api.read, iter((123.5, after))
                api.read = lambda name: next(clocks) if name == transport.NATIVE_CLOCK else read(name)
                helper.start()
                with self.assertRaises(transport.SnapshotError):
                    helper.capture()
                self.assertTrue(helper.failed)
                helper.close()

    def test_requires_explicit_start_and_closes_all_threads(self):
        api, helper = self.make()
        with self.assertRaisesRegex(transport.SnapshotError, 'not active'):
            helper.capture()
        helper.start()
        helper.capture()
        helper.close()
        self.assertEqual(api.active, 0)
        self.assertTrue(all(not thread.is_alive() for thread in api.worker_threads))
        with self.assertRaisesRegex(transport.SnapshotError, 'not active'):
            helper.capture()


if __name__ == '__main__':
    unittest.main()
