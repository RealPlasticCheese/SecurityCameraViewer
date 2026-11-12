#!/usr/bin/env bash
set -euo pipefail
repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
process_id= mode=trace duration_seconds=60 output_dir= tool_path= max_histograms=64 trace_buffer_megabytes=256
usage() {
    echo 'Usage: bash profile-web.sh --process-id PID [--mode trace|allocations|metrics] [--duration-seconds N]'
    echo '       [--output-directory PATH] [--tool-path PATH] [--max-histograms N] [--trace-buffer-megabytes N]'
}
fail() { echo "$*" >&2; exit 1; }
positive_integer() { [[ $1 =~ ^[0-9]+$ && ${#1} -le 10 ]] && ((10#$1 > 0 && 10#$1 <= 2147483647)); }
while (($#)); do
    case "$1" in
        --help|-h) usage; exit 0 ;;
        --process-id|--mode|--duration-seconds|--output-directory|--tool-path|--max-histograms|--trace-buffer-megabytes)
            (($# >= 2)) || fail "Missing value for $1"
            case "$1" in
                --process-id) process_id=$2 ;;
                --mode) mode=$2 ;;
                --duration-seconds) duration_seconds=$2 ;;
                --output-directory) output_dir=$2 ;;
                --tool-path) tool_path=$2 ;;
                --max-histograms) max_histograms=$2 ;;
                --trace-buffer-megabytes) trace_buffer_megabytes=$2 ;;
            esac
            shift 2 ;;
        *) fail "Unknown option: $1" ;;
    esac
done
positive_integer "$process_id" || fail 'Specify a positive --process-id.'
positive_integer "$duration_seconds" || fail 'Specify positive --duration-seconds.'
positive_integer "$max_histograms" || fail 'Specify positive --max-histograms.'
positive_integer "$trace_buffer_megabytes" || fail 'Specify positive --trace-buffer-megabytes.'
case "$mode" in trace|allocations|metrics) ;; *) fail 'Mode must be trace, allocations, or metrics.' ;; esac
process_id=$((10#$process_id))
duration_seconds=$((10#$duration_seconds))
max_histograms=$((10#$max_histograms))
trace_buffer_megabytes=$((10#$trace_buffer_megabytes))
tool_name=dotnet-trace extension=nettrace
if [[ $mode == metrics ]]; then tool_name=dotnet-counters; extension=csv; fi
tool_path=${tool_path:-"$repo_dir/artifacts/profiling-tools/$tool_name"}
[[ -x $tool_path ]] || fail "Missing $tool_name. Install the tools using docs/profiling.md, or specify --tool-path."
output_dir=${output_dir:-"$repo_dir/artifacts/profiling/$(date -u +%Y%m%d-%H%M%S)-$$"}
[[ ! -e $output_dir/$mode.$extension && ! -e $output_dir/capture-$mode.txt ]] || fail 'Capture output already exists. Choose a new output directory.'
tool_version=$("$tool_path" --version) || fail "$tool_name could not report its version."
mkdir -p -- "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)
output_path=$output_dir/$mode.$extension
printf -v duration_argument '%02d:%02d:%02d:%02d' "$((duration_seconds / 86400))" "$((duration_seconds / 3600 % 24))" "$((duration_seconds / 60 % 60))" "$((duration_seconds % 60))"
capture_arguments=(collect --process-id "$process_id" --duration "$duration_argument" --output "$output_path")
if [[ $mode == metrics ]]; then
    capture_arguments+=(--format csv --counters System.Runtime,SecurityCameraViewer.Processing --maxHistograms "$max_histograms")
else
    profile=dotnet-common,dotnet-sampled-thread-time
    if [[ $mode == allocations ]]; then profile=gc-verbose; fi
    capture_arguments+=(--profile "$profile" --buffersize "$trace_buffer_megabytes")
fi
metadata_path=$output_dir/capture-$mode.txt
printf '%s\n' "startedUtc=$(date -u +%Y-%m-%dT%H:%M:%SZ)" "processId=$process_id" "mode=$mode" "durationSeconds=$duration_seconds" \
    "toolVersion=$tool_version" "os=$(uname -srm)" "maxHistograms=$max_histograms" "traceBufferMegabytes=$trace_buffer_megabytes" > "$metadata_path"
echo "Capturing $mode for $duration_seconds seconds into $output_path"
capture_exit_code=0
"$tool_path" "${capture_arguments[@]}" || capture_exit_code=$?
printf 'exitCode=%s\n' "$capture_exit_code" >> "$metadata_path"
exit "$capture_exit_code"
