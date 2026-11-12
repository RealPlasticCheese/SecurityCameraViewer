# Local model descriptions and object rules

The web server discovers compatible ONNX models from data-only `*.model.json` descriptions in its model folder. Select **Refresh models** in Motion & recording settings after adding or replacing files. The browser generates classes, numeric controls and presets from the catalogue. Camera rules remain saved per configuration; refresh never applies a preset automatically. Loaded inference sessions are cleared during refresh.

## Object rules and defaults

Choose a role for each supported object type:

| Role | Actions | Live overlay |
|---|---|---|
| Trigger | Can start or extend its configured zone actions after policy qualification. | Class, confidence and method; green AI outline. |
| Ignore | Cannot start or extend an event. Other objects can still trigger. | Class, confidence and method; dotted gray outline and **Ignored** label. |
| No action | Does not qualify actions. | Omitted. |

For example, person = Trigger and dog = Ignore permits a person and dog in the same scene to record. Ignoring does not subtract pixels from adaptive motion detection. Use **AI** or **Adaptive background subtraction + AI** for class rules. Hybrid mode still requires recent motion in the object's zone and cannot recover motion its gate misses. Schedules, exclusion zones and selected zone actions still apply. Ignore-only rules are allowed for diagnostic overlays.

All three built-in models expose the full ordered COCO 80-class label list. Their default **People and all vehicles** preset triggers people plus every supported vehicle class: bicycle (pushbike), car, motorcycle, airplane, bus, train, truck and boat. No types are ignored by default. New camera settings use the same selection. Their selectable **Original defaults (people and cars)** preset retains the previous person/car selection. Both presets use CPU, confidence 0.45, one check/second, IoU 0.45 and maximum 300 decoded detections. Existing saved camera values are retained; choose **People and all vehicles** and save to expand an existing camera's rules. Editing a value displays **Custom settings**; selecting a preset explicitly replaces that camera's AI rules, options and processing device.

The existing `aiClasses` field remains the trigger list. `aiIgnoreClasses` is the ignore list. `aiConfidence` and `aiFramesPerSecond` retain their existing fields. Advanced values live in `aiOptions`. A type cannot occur in both lists, and at least one trigger or ignore type must be selected. The save APIs validate these values against the selected registered model. Missing descriptions do not prevent loading saved settings; the camera reports an AI analysis error until its definition is restored or another model is selected.

## Add a model without source changes

1. Copy a trusted compatible ONNX file into the configured folder (`models` beside the application, or `--ai-model-directory /absolute/path`).
2. Add a JSON description there, such as `parcel.model.json` below. Use the actual label order, input size, output adapter and license of your model.
3. Click **Refresh models**. Check notices, select the model and a preset, adjust rules, then save.
4. Enable **Show detection overlays** and test the model on your camera footage. Installation is a file-presence check; tensor compatibility is checked when inference starts.

```json
{
  "version": 1,
  "id": "parcel-camera",
  "name": "Parcel camera",
  "fileName": "parcel.onnx",
  "inputSize": 640,
  "adapter": "ultralytics-raw",
  "license": "Your model's license",
  "labels": ["parcel", "cat", "person"],
  "options": [
    { "id": "confidence", "label": "Confidence", "minimum": 0.1, "maximum": 0.95, "step": 0.01, "default": 0.45, "help": "Higher scores reduce false positives but can miss small objects." },
    { "id": "framesPerSecond", "label": "AI checks per second", "minimum": 0.2, "maximum": 2, "step": 0.1, "default": 1, "help": "Increase gradually while watching inference latency." },
    { "id": "iouThreshold", "label": "Duplicate suppression overlap", "minimum": 0.1, "maximum": 0.9, "step": 0.05, "default": 0.45, "help": "Lower values merge more overlapping boxes of the same class." },
    { "id": "maxDetections", "label": "Maximum decoded objects", "minimum": 1, "maximum": 300, "step": 1, "default": 300, "help": "Limits objects retained by the adapter; overlays show at most 64 AI objects." }
  ],
  "presets": [
    { "id": "deliveries", "name": "Deliveries, ignore cats", "triggerClasses": ["parcel", "person"], "ignoreClasses": ["cat"], "device": "cpu", "options": { "confidence": 0.45, "framesPerSecond": 1 } },
    { "id": "animals", "name": "Cats only", "triggerClasses": ["cat"], "ignoreClasses": [], "device": "cpu", "options": { "confidence": 0.5, "framesPerSecond": 1 } }
  ]
}
```

`labels` is an ordered array of unique class names, not a map of aliases. It supports 1–1000 classes. With `labelSet: "coco"`, the standard 80-class list is used when explicit labels are absent. Explicit labels take precedence. If labels or input size are omitted, the registry reads ONNX `names` and `imgsz` metadata without loading weights into an inference session. Names may be a JSON array, a contiguous integer-key JSON map, or a simple integer-key Python dictionary of quoted strings; no expressions are executed. Input metadata must describe two equal dimensions. Models declaring a different `task` are rejected when metadata is used. Explicit descriptions still undergo tensor validation during inference.

The adapter is always explicit: labels alone do not identify the model's input/output layout. All adapters require one float32 NCHW input, fixed batch one and a square input size from 32–1280 in multiples of 32; outputs must also be float32 and single-tensor.

| Adapter | Input preparation | Supported output |
|---|---|---|
| `yolox` | Top-left padded BGR, unnormalised float values; raw grid decoding. | `[1,N,5+C]`, where C is the described class count. |
| `ultralytics-raw` | Centred letterbox RGB, values scaled to 0–1. | `[1,4+C,N]`. |
| `ultralytics-end-to-end` | Centred letterbox RGB, values scaled to 0–1. | `[1,N,6]`: xmin, ymin, xmax, ymax, score, class index. |
| `ultralytics` | Same as the two explicit Ultralytics adapters. | Automatic raw/end-to-end selection; use an explicit adapter for ambiguous dimensions. |

Matching these contracts allows a new model to work without C# or browser changes. Other preprocessing, output layouts, segmentation, tracking and additional executable options need an adapter/implementation extension. A manifest cannot invent executable behavior. Verify export class order and output contract before enabling camera actions.

## Supported numeric settings

Descriptions can customise labels, help, defaults, step sizes and narrow the following bounds. Confidence and frame rate must be present when supplying an options list; the other two are optional. Omit the list to use all four defaults.

| ID | Allowed bounds | Default |
|---|---|---|
| `confidence` | 0.1–0.95 | 0.45 |
| `framesPerSecond` | 0.2–5 | 1 |
| `iouThreshold` | 0.1–0.9 | 0.45 |
| `maxDetections` | Integer 1–300 | 300 |

Preset options omitted from the preset use description defaults. If presets are omitted entirely, a custom model gets a preset that triggers all its supported objects. Up to 16 presets can be described; IDs must be unique and `custom` is reserved. Devices are `cpu`, `gpu` or `auto`, with the same runtime availability/fallback policy as built-in models.

Invalid descriptions are isolated with administrator notices. Unknown JSON fields/options, duplicate model IDs (including built-in IDs), paths instead of plain ONNX filenames and invalid labels/ranges are rejected. Discovery reads the first 32 description files in filename order, each up to 256 KB. ONNX metadata reads are bounded and skip graph/weight payloads; full model validity is checked by the runtime.

## Services and limits

`IAiModelRegistry` owns description discovery and validation. `IAiModelMetadataReader` reads embedded metadata. `IObjectModelAdapter` owns family-specific preprocessing/decoding. Shared `IAiInferenceService` uses those definitions and `IModelSessionFactory` for CPU/GPU execution. `AiMotionDetector` applies camera-specific trigger/ignore rules and zones; the browser renders the generic observation `disposition` and evidence. No service per model is needed.

Trigger and ignore classes are decoded in the same inference pass. One shared inference lane and a maximum of six cached model/device sessions bound concurrency and session memory regardless of how many descriptions are registered. AI observations remain limited to 64 per camera (trigger objects first); adaptive + AI observations remain limited to 128 total. Different models can therefore be installed without growing the cache indefinitely. Refresh takes that lane while clearing sessions, so a pending inference finishes first.

`GET /api/recording/detection` returns the administrator catalogue. `POST /api/recording/detection/reload` refreshes it and returns the updated catalogue. Both require administrator access when authentication is configured. `/api/detections` and `/api/v1/detections` continue to require read/admin scope and omit model filesystem paths. Their observations include `disposition: "trigger"` or `"ignore"`; ignored observations always have `confirmed: false`.

For CPU/GPU installation, small-server tuning and zone policy, see [motion recording](motion-recording.md) and the in-app **?** guide.
