import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("adaptive_benchmarks", Path(__file__).resolve().parents[1] / "tools/compare_adaptive_benchmarks.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class ComparisonTests(unittest.TestCase):
    def samples(self):
        return [{"build": label, "data": {"results": [{"name": "test", "millisecondsPerOperation": elapsed, "bytesPerOperation": 32}]}}
                for label, elapsed in (("before", 2), ("after", 1), ("before", 4), ("after", 2), ("before", 3), ("after", 1.5))]

    def test_medians_ranges_and_reduction(self):
        result = benchmark.summarize(self.samples())[0]
        self.assertEqual(result["before"], {"medianMilliseconds": 3, "minimumMilliseconds": 2, "maximumMilliseconds": 4, "medianBytes": 32})
        self.assertEqual(result["after"]["medianMilliseconds"], 1.5)
        self.assertEqual(result["reductionPercent"], 50)

    def test_regression_is_negative(self):
        samples = self.samples()
        for sample in samples:
            if sample["build"] == "after":
                sample["data"]["results"][0]["millisecondsPerOperation"] *= 4
        self.assertEqual(benchmark.summarize(samples)[0]["reductionPercent"], -100)

    def test_empty_unbalanced_and_unknown_builds(self):
        for samples in ([], self.samples()[:-1], [{"build": "unknown"}], self.samples()[::2]):
            with self.subTest(samples=samples), self.assertRaises(ValueError):
                benchmark.summarize(samples)

    def test_missing_and_duplicate_cases(self):
        for mutate in (lambda cases: cases.clear(), lambda cases: cases.append(copy.deepcopy(cases[0])), lambda cases: cases[0].update(name="other")):
            samples = self.samples()
            mutate(samples[-1]["data"]["results"])
            with self.assertRaises(ValueError):
                benchmark.summarize(samples)

    def test_nonfinite_negative_and_zero_measurements(self):
        for field, values in (("millisecondsPerOperation", [0, -1, float("nan"), float("inf")]), ("bytesPerOperation", [-1, float("nan"), float("inf")])):
            for value in values:
                samples = self.samples()
                samples[0]["data"]["results"][0][field] = value
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    benchmark.summarize(samples)

    def test_zero_allocations_are_valid(self):
        samples = self.samples()
        for sample in samples:
            sample["data"]["results"][0]["bytesPerOperation"] = 0
        self.assertEqual(benchmark.summarize(samples)[0]["before"]["medianBytes"], 0)

    def test_argument_and_missing_build_failures(self):
        for runs, iterations in ((0, 1), (1, 0), (-1, 1), (1, -1)):
            with self.assertRaises(ValueError):
                benchmark.compare("missing", "missing", runs, iterations)
        with self.assertRaises(FileNotFoundError):
            benchmark.compare("missing", "missing", 1, 1)


if __name__ == "__main__":
    unittest.main()
