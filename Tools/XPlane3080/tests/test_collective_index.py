"""Main-axis regression cases; no simulator or network calls."""
import math
import unittest
from urllib.error import HTTPError
from unittest.mock import Mock

from test_reliability import Api, Clock
import test_reliability as legacy
import flight_demo as demo


class IndexedTransportTests(unittest.TestCase):
    def api(self, data):
        api = demo.NativeApi()
        api.ids[demo.PROP_HANDLES] = 42
        api.request = Mock(return_value={'data': data})
        return api

    def test_indexed_get_uses_query_and_unwraps_one_element_array(self):
        api = self.api([.431])
        self.assertEqual(api.read_index(demo.PROP_HANDLES, 0), .431)
        api.request.assert_called_once_with('/datarefs/42/value?index=0', timeout=None)

    def test_indexed_get_rejects_scalar_wrong_size_and_non_numeric_results(self):
        for data in (.5, [], [.4, .6], [None], ['.5'], [True], [math.nan], [math.inf]):
            with self.subTest(data=data):
                api = self.api(data)
                with self.assertRaises(demo.DemoError):
                    api.read_index(demo.PROP_HANDLES, 0)
                self.assertEqual(api.request.call_count, 1)

    def test_missing_indexed_read_refreshes_identifier_once(self):
        api = self.api([.5])
        missing = HTTPError('unused', 404, 'missing', None, None)
        api.request.side_effect = [missing,
            {'data': [{'name': demo.PROP_HANDLES, 'id': 99}]}, {'data': [.5]}]
        try:
            self.assertEqual(api.read_index(demo.PROP_HANDLES, 0), .5)
        finally:
            missing.close()
        self.assertEqual(api.request.call_args_list[-1].args[0], '/datarefs/99/value?index=0')
        self.assertEqual(api.request.call_count, 3)

    def test_indexed_patch_sends_only_one_scalar_and_never_retries(self):
        api = self.api(None)
        api.write_index(demo.PROP_HANDLES, 0, .48)
        api.request.assert_called_once_with('/datarefs/42/value?index=0', {'data': .48}, 'PATCH')
        api.request.reset_mock(side_effect=True)
        api.request.side_effect = OSError('uncertain mutation')
        with self.assertRaises(OSError):
            api.write_index(demo.PROP_HANDLES, 0, .49)
        self.assertEqual(api.request.call_count, 1)

    def test_bad_index_or_value_is_refused_before_request(self):
        api = self.api([.5])
        for index in (-1, True, .0, '0'):
            with self.subTest(index=index):
                with self.assertRaises(demo.DemoError):
                    api.read_index(demo.PROP_HANDLES, index)
                with self.assertRaises(demo.DemoError):
                    api.write_index(demo.PROP_HANDLES, index, .5)
        for value in (True, '.5', math.nan, math.inf):
            with self.subTest(value=value), self.assertRaises(demo.DemoError):
                api.write_index(demo.PROP_HANDLES, 0, value)
        api.request.assert_not_called()


class MainCollectiveTests(unittest.TestCase):
    # Share only the existing temporary-directory controller runner, not its tests.
    run_controller = legacy.ReliabilityTests.run_controller

    def test_report_observes_main_when_aggregate_tracks_tail(self):
        class MovingTail(Api):
            def read(self, name, timeout=None):
                if name == 'sim/cockpit2/engine/actuators/prop_ratio_all':
                    self.values[demo.PROP_HANDLES][1] = .8 + .1 * math.sin(self.clock.now)
                    return sum(self.values[demo.PROP_HANDLES][:2]) / 2
                return super().read(name, timeout)
        clock = Clock()
        api = MovingTail(clock)
        api.values[demo.PROP_HANDLES][0] = .431
        status, report = self.run_controller(api, clock)
        self.assertEqual(status, 0)
        self.assertEqual(report['collective'], .431)
        self.assertEqual(report['values']['mainCollective'], .431)
        self.assertNotEqual(report['aggregatePropRatio'], report['collective'])
        self.assertEqual(report['collectiveDataref'], demo.PROP_HANDLES)
        self.assertEqual(report['collectiveIndex'], 0)
        self.assertFalse(any(a[0] == 'write_index' for a in api.actions))

    def test_assist_seed_and_writes_do_not_use_or_change_tail(self):
        class DistinctTail(Api):
            def read(self, name, timeout=None):
                if name == 'sim/cockpit2/engine/actuators/prop_ratio_all':
                    return sum(self.values[demo.PROP_HANDLES][:2]) / 2
                return super().read(name, timeout)
        clock = Clock()
        api = DistinctTail(clock)
        api.values[demo.PROP_HANDLES][:2] = [.5, 1.]
        api.values[demo.P + 'indicated_airspeed'] = 90
        status, _ = self.run_controller(api, clock, collective_assist=True)
        self.assertEqual(status, 0)
        writes = [a for a in api.actions if a[0] == 'write_index']
        self.assertTrue(writes)
        self.assertTrue(all(a[:3] == ('write_index', demo.PROP_HANDLES, 0) for a in writes))
        self.assertEqual(writes[0][3], .5)  # Not the aggregate's .75 seed.
        self.assertEqual(api.values[demo.PROP_HANDLES][1], 1.)
        self.assertFalse(any(a[:2] == ('write', demo.PROP_HANDLES) or
                             a[:2] == ('write', 'sim/cockpit2/engine/actuators/prop_ratio_all')
                             for a in api.actions))

    def test_invalid_main_cannot_be_hidden_by_valid_aggregate(self):
        class ChangedMain(Api):
            def read_index(self, name, index, timeout=None):
                value = super().read_index(name, index, timeout)
                self.values[demo.PROP_HANDLES][0] = 1.2
                return value
        clock = Clock()
        api = ChangedMain(clock)
        status, report = self.run_controller(api, clock, collective_assist=True)
        self.assertEqual(status, 1)
        self.assertIn('MAIN collective outside', report['reason'])
        self.assertTrue(report['pauseConfirmed'])
        self.assertFalse(any(a[0] == 'write_index' for a in api.actions))

    def test_indexed_read_time_is_included_in_sample_freshness(self):
        class SlowMain(Api):
            def read_index(self, name, index, timeout=None):
                value = super().read_index(name, index, timeout)
                self.clock.sleep(4)
                return value
        clock = Clock()
        api = SlowMain(clock)
        status, report = self.run_controller(api, clock, collective_assist=True)
        self.assertEqual(status, 0)
        self.assertIn('native sample exceeded age', report['reason'])
        self.assertTrue(report['pauseConfirmed'])
        self.assertFalse(any(a[0] == 'write_index' for a in api.actions))


if __name__ == '__main__':
    unittest.main()
