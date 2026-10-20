# Power meter definitions (`data/meters/<model>.json`)

Each file describes one power meter and the performance tests from its manual that the HP 11683A Range Calibrator
can drive. `meter-test` lists every file in `data/meters` and runs the one the operator picks. To add a meter, write a
file in this format: the app checks it on load and refuses one it cannot run.

## Meter

| Field | Meaning |
|---|---|
| `model` | The model, e.g. `437B`. Also what `--meter` matches. |
| `manufacturer`, `title` | For the record, e.g. `HP`, `HP 437B Power Meter`. |
| `manual` | The manual (file name, title, part number) the tests were transcribed from. |
| `busDriver` | `E4418B`, `E4419B`, `437B`, `438A`, `436A`, or `null` when the meter has no bus. |
| `defaultResource` | VISA resource used when `powerMeter.resource` is not set. |
| `channels` | `["A"]`, or `["A", "B"]` for a dual-channel meter. |
| `warmupMinutes` | From the manual's performance-test conditions. |
| `notes` | Shown before the tests. |
| `tests` | See below. |
| `notCovered` | Tests in the manual this app does not run: `title`, `source`, `equipment` (what they need). |
| `ambiguities` | Where editions or OCR disagree, and which value the file uses. Printed on the record. |

## Test

| Field | Meaning |
|---|---|
| `id` | Unique in the file, kebab-case. |
| `title`, `source` | As in the manual, with paragraph and page (printed and PDF). |
| `reading` | Where readings come from: `Meter` (over the bus if the meter has a driver, otherwise typed), `Recorder` (the bench DVM on the meter's rear RECORDER OUTPUT), or `Operator` (always typed). |
| `perChannel` | Run once per selected channel. |
| `equipment`, `setup`, `notes` | Shown to the operator before the rows: the manual's equipment and set-up steps, condensed but faithful. |
| `points` | The table rows, in the manual's order. |

## Point (row)

| Field | Meaning |
|---|---|
| `label` | The row's name in the manual's table, e.g. `3 µW`, `RNG 2`, `CAL FACTOR 95%`. |
| `calibrator` | The 11683A's switches: `range` (`3uW` … `100mW`), `function` (`Calibrate` / `Standby`), `polarity` (`Normal` / `Reverse`), and for Option H01 `reference: "Ext"` with `externalVolts` (the DC calibrator on REF EXT). `null` when the 11683A is not used for the row (e.g. a power sensor with no RF). |
| `meterSetting` | What to set on the meter, in the operator's words. |
| `meterRange` | The meter's RANGE position where it decides the recorder output's full scale (435A/435B). |
| `calFactorPercent` | The meter's CAL FACTOR setting, when the test steps it. |
| `expected` | The nominal reading. Where the manual prints only limits, the file says how it was derived. |
| `unit` | `W`, `mW`, `uW`, `nW`, `dBm`, `dB`, `mV` or `V`. |
| `min`, `max` | The manual's limits, as printed (a reversed pair is corrected and noted). |
| `reading` | Overrides the test's reading source for this row, e.g. `Operator` for a dBm display. |
| `note` | Anything specific to the row. |

Rules the loader enforces:
- units must be one of the above;
- `min` ≤ `max`;
- ranges must be 11683A positions;
- `Recorder` rows must be in mV or V;
- `Meter` rows must be in a power unit the bus driver's watts can be converted to (`dB` is relative, so a dB
  row must be `Operator`).

A reading passes when it is within `min`…`max`; there is no guard band, because these are the manuals' own
acceptance limits.
