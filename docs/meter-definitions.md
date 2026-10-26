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
| `meter` | The meter's settings after Preset (see *Meter settings*). With it, a meter on the bus is set up by the app and the operator only sets the 11683A; without it, the operator follows `setup`. |
| `zero` | The zero: `calibrator` (the 11683A's switches for it, normally STANDBY), `settleSeconds` (the least wait before it, e.g. 120: twice the manuals' "typically under 60 s" STANDBY drift), and optionally `waitAfterSeconds` (a wait after it before the first reading, e.g. 60 for the E441x Zero Test's 30 s, doubled) and `checkWithinMicrowatts` (after it the meter must read within ± this; outside, the zero is offered again, e.g. 0.05 for E441x Procedures 1 and 2). |
| `cal` | The CAL: `calibrator` (normally CALIBRATE, 1 mW), `referenceCalFactorPercent` (default 100), and `adjustTo` when CAL is a front-panel adjustment (436A CAL ADJ, e.g. `1.000 ±0.002 mW`): the operator turns it while the app shows the reading. |

## Meter settings

Used by a test's `meter` and a row's `meter`; a row's settings go on top of the test's, and a setting left out is
left as it is.

| Field | Meaning |
|---|---|
| `display` | `Watts`, `Dbm` or `DbRelative`. |
| `range` | A fixed range, 1 the most sensitive; `0` for autorange. |
| `filter` | The averaging length; `0` for the meter's automatic filter. |
| `resolution` | The display resolution in the meter's terms (E441x 1–4). |
| `storeReference` | Before reading, store the present input as the dB REL reference (436A dB REF, 438A REL). |

A driver refuses a setting it has no bus code for, so a file can't ask for something the meter won't do.

## Point (row)

| Field | Meaning |
|---|---|
| `label` | The row's name in the manual's table, e.g. `3 µW`, `RNG 2`, `CAL FACTOR 95%`. |
| `calibrator` | The 11683A's switches: `range` (`3uW` … `100mW`), `function` (`Calibrate` / `Standby`), `polarity` (`Normal` / `Reverse`), and for Option H01 `reference: "Ext"` with `externalVolts` (the DC calibrator on REF EXT). `null` when the 11683A is not used for the row (e.g. a power sensor with no RF). |
| `meterSetting` | What to set on the meter, in the operator's words. |
| `meterRange` | The meter's RANGE position where it decides the recorder output's full scale (435A/435B). |
| `calFactorPercent` | The meter's CAL FACTOR setting, when the test steps it. |
| `meter` | The row's meter settings over the bus, on top of the test's, e.g. `{"range": 3}` or `{"display": "Dbm"}`. |
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
- `Meter` rows must match the meter's display: a power unit (or dBm) with `Watts`, dBm with `Dbm`, dB with
  `DbRelative`;
- a `zero` or `cal` needs the test's `meter`.

A reading passes when it is within `min`…`max`; there is no guard band, because these are the manuals' own
acceptance limits.
