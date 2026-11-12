"""Validate the compact NUC report analyzer, including rounded timestamps and absent baseline counters."""
import csv
import math
from pathlib import Path
import runpy
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
ANALYSIS = runpy.run_path(str(ROOT / 'tools/profiling/compare.py'))


class NucProfileAnalysisTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.folder = Path(self.directory.name)

    def fixture(self, native=False):
        (self.folder / 'capture-metrics.txt').write_text('durationSeconds=2\nexitCode=0\n')
        rows = []

        def metric(name, values, counter_type='Metric', repeated_timestamp=False):
            for index, value in enumerate(values):
                second = 1 if repeated_timestamp else index + 1
                rows.append([f'2026-10-10 00:00:0{second}', 'Test', name, counter_type, value])

        metric('dotnet.process.cpu.count ({cpu})', [4, 4], repeated_timestamp=True)
        metric('dotnet.process.cpu.time (s / 1 sec)[cpu.mode=user]', [0.2, 0.4], 'Rate')
        metric('dotnet.process.cpu.time (s / 1 sec)[cpu.mode=system]', [0.1, 0.1], 'Rate')
        metric('dotnet.process.memory.working_set (By)', [1048576, 2097152], repeated_timestamp=True)
        metric('dotnet.gc.heap.total_allocated (By / 1 sec)', [1000000, 2000000], 'Rate')
        metric('dotnet.gc.pause.time (s / 1 sec)', [0.1, 0.2], 'Rate')
        metric('dotnet.thread_pool.queue.length ({work_item} / 1 sec)', [0, 0], 'Rate')
        metric('scv.frames ({frame} / 1 sec)[stage=analysis.captured]', [10, 20], 'Rate')
        metric('scv.frames ({frame} / 1 sec)[stage=analysis.dropped]', [1], 'Rate')
        if native:
            for pipeline, cpu in [('preview', [0.8, 0.6]), ('live.transcode', [0, 0])]:
                tags = f'[channel=2;pipeline={pipeline}]'
                metric('scv.ffmpeg.cpu.time (s / 1 sec)' + tags, cpu, 'Rate')
                metric('scv.ffmpeg.processes ({process})' + tags, [1, 1] if pipeline == 'preview' else [0, 0])
                metric('scv.ffmpeg.working_set (By)' + tags, [1048576, 1048576] if pipeline == 'preview' else [0, 0])
        self.write_rows(rows)
        return rows

    def write_rows(self, rows):
        with (self.folder / 'metrics.csv').open('w', newline='', encoding='utf-8') as stream:
            writer = csv.writer(stream)
            writer.writerow(['Timestamp', 'Provider', 'Counter Name', 'Counter Type', 'Mean/Increment'])
            writer.writerows(rows)

    def test_baseline_does_not_invent_native_cpu(self):
        self.fixture()
        result = ANALYSIS['capture'](self.folder)
        self.assertFalse(result['native_cpu_available'])
        self.assertIsNone(result['combined_application_cpu_cores'])
        self.assertIsNone(result['combined_application_cpu_percent_of_host'])
        self.assertAlmostEqual(result['server_cpu_cores']['mean'], 0.4)
        self.assertAlmostEqual(result['server_cpu_percent_of_host'], 10)

    def test_native_pipeline_and_host_cpu_reconcile(self):
        self.fixture(native=True)
        result = ANALYSIS['capture'](self.folder)
        self.assertAlmostEqual(result['combined_application_cpu_cores']['mean'], 1.1)
        self.assertAlmostEqual(result['combined_application_cpu_percent_of_host'], 27.5)
        self.assertAlmostEqual(result['native_pipelines']['preview']['cpu_percent_of_host'], 17.5)
        self.assertAlmostEqual(result['native_pipelines']['preview']['cpu_percent_of_tracked_application'], 100 * 0.7 / 1.1)
        self.assertEqual(result['native_pipelines']['live.transcode']['cpu_cores']['max'], 0)

    def test_repeated_rounded_timestamps_preserve_gauge_observations(self):
        self.fixture()
        result = ANALYSIS['capture'](self.folder)
        self.assertEqual(result['duplicate_rounded_timestamps'], 2)
        self.assertEqual(result['server_working_set_mib']['samples'], 2)
        self.assertEqual(result['server_working_set_mib']['mean'], 1.5)
        self.assertEqual(result['server_working_set_mib']['first'], 1)
        self.assertEqual(result['server_working_set_mib']['last'], 2)

    def test_sparse_events_use_full_capture_duration(self):
        self.fixture()
        result = ANALYSIS['capture'](self.folder)
        self.assertEqual(result['frames']['analysis.dropped'], {'total': 1, 'per_second': 0.5})
        self.assertEqual(result['frames']['analysis.captured']['total'], 30)
        self.assertEqual(result['allocation_mb_per_second']['mean'], 1.5)
        self.assertAlmostEqual(result['gc_pause_seconds']['sum'], 0.3)

    def test_failed_capture_is_rejected(self):
        self.fixture()
        (self.folder / 'capture-metrics.txt').write_text('durationSeconds=2\nexitCode=1\n')
        with self.assertRaisesRegex(ValueError, 'did not complete'):
            ANALYSIS['capture'](self.folder)

    def test_missing_headers_are_rejected(self):
        self.fixture()
        (self.folder / 'metrics.csv').write_text('Timestamp,Value\n')
        with self.assertRaisesRegex(ValueError, 'missing CSV columns'):
            ANALYSIS['capture'](self.folder)

    def test_non_finite_values_are_rejected(self):
        for value in [math.nan, math.inf, -math.inf]:
            with self.subTest(value=value):
                rows = self.fixture()
                rows[0][-1] = value
                self.write_rows(rows)
                with self.assertRaisesRegex(ValueError, 'non-finite'):
                    ANALYSIS['capture'](self.folder)

    def test_non_second_rate_is_rejected(self):
        rows = self.fixture()
        rows[2][2] = 'dotnet.process.cpu.time (s / 5 sec)[cpu.mode=user]'
        self.write_rows(rows)
        with self.assertRaisesRegex(ValueError, 'one-second intervals'):
            ANALYSIS['capture'](self.folder)

    def test_incomplete_sample_coverage_is_rejected(self):
        self.fixture()
        (self.folder / 'capture-metrics.txt').write_text('durationSeconds=3\nexitCode=0\n')
        with self.assertRaisesRegex(ValueError, 'duration and sample count'):
            ANALYSIS['capture'](self.folder)

    def test_unequal_aggregation_coverage_is_rejected(self):
        rows = self.fixture(native=True)
        rows = [row for row in rows if not (row[0].endswith('02') and 'cpu.time' in row[2] and 'pipeline=preview' in row[2])]
        self.write_rows(rows)
        with self.assertRaisesRegex(ValueError, 'same sample intervals'):
            ANALYSIS['capture'](self.folder)

    def test_empty_stats_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'empty series'):
            ANALYSIS['stats']([])

    def test_single_sample_percentile_and_timestamp_formats(self):
        self.assertEqual(ANALYSIS['stats']([3])['p95'], 3)
        self.assertEqual(ANALYSIS['timestamp']('10/10/2026 00:00:01'), ANALYSIS['timestamp']('2026-10-10 00:00:01'))
        with self.assertRaisesRegex(ValueError, 'Unsupported CSV timestamp'):
            ANALYSIS['timestamp']('invalid')


if __name__ == '__main__':
    unittest.main()
