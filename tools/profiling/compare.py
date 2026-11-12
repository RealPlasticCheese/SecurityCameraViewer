"""Summarize two one-second dotnet-counters captures without copying raw CSVs into Git."""
import argparse
import collections
import csv
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import statistics

MIB = 1024 * 1024


def timestamp(value):
    for pattern in ('%m/%d/%Y %H:%M:%S', '%Y-%m-%d %H:%M:%S'):
        try:
            return datetime.strptime(value, pattern)
        except ValueError:
            pass
    raise ValueError(f'Unsupported CSV timestamp: {value}')


def stats(values):
    ordered = sorted(values)
    if not ordered:
        raise ValueError('Cannot summarize an empty series')
    return {
        'samples': len(values), 'mean': statistics.mean(values),
        'median': statistics.median(values), 'p95': ordered[math.ceil(len(ordered) * 0.95) - 1],
        'min': ordered[0], 'max': ordered[-1], 'sum': sum(values),
        'first': values[0], 'last': values[-1],
    }


def aggregate(series, prefix, tag=None, scale=1):
    selected = [samples for name, samples in series.items()
        if name.startswith(prefix) and (tag is None or tag in name)]
    if not selected:
        return {}
    if any(len(samples) != len(selected[0]) for samples in selected):
        raise ValueError(f'{prefix}: series do not cover the same sample intervals')
    # CSV wall timestamps are rounded to seconds; two valid gauge observations can share a timestamp.
    # Complete series in these captures have equal counts and can be aligned by observation order.
    return {index: sum(sample[1] / scale for sample in interval)
        for index, interval in enumerate(zip(*selected))}


def zero_spans(samples):
    spans = []
    current = None
    for stamp, value in samples:
        if value == 0:
            if current is None:
                current = {'first_csv_timestamp': stamp, 'last_csv_timestamp': stamp, 'samples': 0}
            current['last_csv_timestamp'] = stamp
            current['samples'] += 1
        elif current is not None:
            spans.append(current)
            current = None
    if current is not None:
        spans.append(current)
    return spans


def capture(folder):
    source = folder / 'metrics.csv'
    metadata = dict(line.split('=', 1) for line in (folder / 'capture-metrics.txt').read_text().splitlines())
    if metadata.get('exitCode') != '0':
        raise ValueError(f'{folder}: counter capture did not complete successfully')
    series = collections.defaultdict(list)
    times = set()
    with source.open(encoding='utf-8-sig', newline='') as stream:
        reader = csv.DictReader(stream)
        required = {'Timestamp', 'Provider', 'Counter Name', 'Counter Type', 'Mean/Increment'}
        if not required.issubset(reader.fieldnames or []):
            raise ValueError(f'{source}: missing CSV columns')
        for row in reader:
            value = float(row['Mean/Increment'])
            if not math.isfinite(value):
                raise ValueError(f'{source}: non-finite counter value')
            if row['Counter Type'] == 'Rate' and ' / 1 sec)' not in row['Counter Name']:
                raise ValueError(f'{source}: rate does not use one-second intervals')
            series[row['Counter Name']].append((row['Timestamp'], value))
            times.add(row['Timestamp'])
    stamps = sorted(timestamp(value) for value in times)
    if len(stamps) < 2 or any((later - earlier).total_seconds() != 1 for earlier, later in zip(stamps, stamps[1:])):
        raise ValueError(f'{source}: expected contiguous one-second samples')
    if len(stamps) != int(metadata['durationSeconds']):
        raise ValueError(f'{source}: duration and sample count differ')
    def metric(prefix):
        return stats(list(aggregate(series, prefix).values()))

    logical = metric('dotnet.process.cpu.count')['mean']
    cpu = aggregate(series, 'dotnet.process.cpu.time')
    native = aggregate(series, 'scv.ffmpeg.cpu.time')
    combined = {stamp: value + native[stamp] for stamp, value in cpu.items() if stamp in native}
    result = {
        'source_file': source.name, 'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
        'source_bytes': source.stat().st_size, 'numeric_rows': sum(len(samples) for samples in series.values()),
        'series': len(series), 'capture_metadata': metadata,
        'duplicate_rounded_timestamps': sum(len(samples) - len({stamp for stamp, _ in samples}) for samples in series.values()),
        'first_csv_timestamp': stamps[0].isoformat(sep=' '), 'last_csv_timestamp': stamps[-1].isoformat(sep=' '),
        'logical_processors': logical, 'server_cpu_cores': stats(list(cpu.values())),
        'server_cpu_percent_of_host': statistics.mean(cpu.values()) / logical * 100,
        'server_working_set_mib': stats(list(aggregate(series, 'dotnet.process.memory.working_set', scale=MIB).values())),
        'allocation_mb_per_second': stats(list(aggregate(series, 'dotnet.gc.heap.total_allocated', scale=1e6).values())),
        'gc_pause_seconds': metric('dotnet.gc.pause.time'),
        'native_cpu_available': bool(native),
        'combined_application_cpu_cores': stats(list(combined.values())) if combined else None,
        'combined_application_cpu_percent_of_host': statistics.mean(combined.values()) / logical * 100 if combined else None,
        'frames': {}, 'operations': {}, 'motion_interval_percentiles_ms': {}, 'exceptions': {},
        'native_pipelines': {}, 'native_channels': {},
    }
    for name, samples in sorted(series.items()):
        values = [value for _, value in samples]
        if name.startswith('scv.frames') or name.startswith('scv.processing.operations'):
            stage = name.split('stage=', 1)[1].split(']')[0]
            target = 'frames' if name.startswith('scv.frames') else 'operations'
            result[target][stage] = {'total': sum(values), 'per_second': sum(values) / len(stamps)}
        if name.startswith('scv.processing.duration') and 'stage=motion.total;' in name:
            percentile = name.split('Percentile=', 1)[1].split(']')[0]
            result['motion_interval_percentiles_ms'][percentile] = stats(values)
        if name.startswith('dotnet.exceptions'):
            result['exceptions'][name.split('error.type=', 1)[1].split(']')[0]] = sum(values)
        if name.startswith('scv.ffmpeg.processes'):
            channel = name.split('channel=', 1)[1].split(';')[0]
            pipeline = name.split('pipeline=', 1)[1].split(']')[0]
            key = f'channel={channel};pipeline={pipeline}'
            cpu_samples = aggregate(series, 'scv.ffmpeg.cpu.time', f'[{key}]')
            result['native_channels'][key] = {
                'cpu_cores_mean': statistics.mean(cpu_samples.values()),
                'process_count_mean': statistics.mean(values),
                'zero_process_samples': sum(value == 0 for value in values),
                'zero_process_spans': zero_spans(samples) if any(values) else [],
            }
    pipelines = sorted({key.split('pipeline=', 1)[1] for key in result['native_channels']})
    for pipeline in pipelines:
        tag = f';pipeline={pipeline}]'
        native_cpu = aggregate(series, 'scv.ffmpeg.cpu.time', tag)
        memory = aggregate(series, 'scv.ffmpeg.working_set', tag, MIB)
        children = aggregate(series, 'scv.ffmpeg.processes', tag)
        result['native_pipelines'][pipeline] = {
            'cpu_cores': stats(list(native_cpu.values())),
            'cpu_percent_of_host': statistics.mean(native_cpu.values()) / logical * 100,
            'cpu_percent_of_tracked_application': statistics.mean(native_cpu.values()) / statistics.mean(combined.values()) * 100,
            'working_set_mib_mean': statistics.mean(memory.values()),
            'process_count_mean': statistics.mean(children.values()),
        }
    result['ai_stage_series_present'] = any('stage=ai.' in name for name in series)
    result['thread_pool_queue_delta_sum'] = metric('dotnet.thread_pool.queue.length')['sum']
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--night', type=Path, required=True, help='Previous capture directory')
    parser.add_argument('--day', type=Path, required=True, help='New capture directory')
    parser.add_argument('--output', type=Path, help='Optional compact JSON output; raw files are never copied')
    parser.add_argument('--verify', type=Path, help='Compare recomputed results with a saved compact JSON file')
    arguments = parser.parse_args()
    result = {'night': capture(arguments.night), 'day': capture(arguments.day)}
    result['interpretation'] = {
        'comparison_limit': 'Compare total-application CPU only when both captures include native counters',
        'working_set_limit': 'Summed process resident sets can count shared pages repeatedly',
        'histogram_limit': 'Statistics describe interval percentile readings, not percentiles of all individual operations',
        'sampling': 'Complete equal-length series are aligned by observation order; rounded wall timestamps can repeat',
    }
    if arguments.verify:
        previous = json.loads(arguments.verify.read_text())
        if previous != result:
            raise ValueError('Recomputed summary does not match the retained JSON')
        print('Recomputed summary matches retained JSON, source hashes and capture metadata.')
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    day = result['day']
    print(f'Daytime server and tracked children: {day["combined_application_cpu_percent_of_host"]:.2f}% of host capacity')
    for name, values in day['native_pipelines'].items():
        print(f'{name}: {values["cpu_percent_of_host"]:.2f}% of host; {values["cpu_percent_of_tracked_application"]:.1f}% of tracked application CPU')


if __name__ == '__main__':
    main()
