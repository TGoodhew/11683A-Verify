# `11683A-actuals.json`

The measured output of one HP 11683A, written by 11683A-Verify after a passing Range Switch Performance Test. A
power meter performance test can use these values instead of the 11683A's nominal ones, which narrows that test's
uncertainty.

The file is written to the results directory and replaced by each new export. `source` and `sourceSha256` trace it to
the results file it came from. That results file, and the PDF beside it, hold every raw reading.

## When it is written

Only from a run where:

- Part 1 is complete and passed (every ratio, and the 1 mW voltage);
- Part 2, if it was run, is complete and passed.

A simulated run can be exported so the format can be tried out, but `simulated` is then `true`. **A consumer
must refuse a file with `"simulated": true`.**

## Top level

| Field | Type | Meaning |
|---|---|---|
| `format` | string | `"11683A-actuals/1"`. A consumer should refuse other values. |
| `unit` | object | `model`, `serial`, `options` of the 11683A. |
| `measured` | date-time | When the test started (ISO 8601, with UTC offset). |
| `source` | string | File name of the results JSON it came from. |
| `sourceSha256` | string | SHA-256 (hex) of that results file's text. |
| `dvm` | object | The DVM: `details` (manufacturer, model, serial, firmware, calibration date and interval, due date), `resource`, `identityReply`, `calibrationState` (`InDate`, `PastDue` or `NotEntered`). |
| `coverageFactor` | number | k for every `expandedUncertainty` below (2). |
| `simulated` | bool | True if no instrument was measured. |
| `ratios` | array | Part 1 voltage ratios. |
| `voltages` | array | Part 1 voltages. |
| `resistances` | array | Part 2 resistances (empty if Part 2 was not run). |

## Each value

| Field | Meaning |
|---|---|
| `range` | 11683A RANGE switch position: `100mW`, `30mW`, … `3uW`. |
| `nominalWatts` | The position's nominal full-scale power in W (0.1, 0.03, …). The "3" positions are marked 3 but are really √10 steps (e.g. 3.162 µW). |
| `quantity` | `Ratio`, `DcVoltage` or `FourWireResistance`. |
| `unit` | `""` for ratios, `"V"`, `"Ω"`. |
| `value` | The measured value: the mean of N readings on a fixed DVM range. |
| `expandedUncertainty` | U = k·u_c, in the same unit. u_c combines the DVM spec (rectangular, spec/√3) with s/√N. Ratios combine the two readings' relative uncertainties by RSS (or linearly, if the run's settings say so). |
| `nominal` | The middle of the manual's limits. `NaN` where the manual gives no limit. |
| `deviation` | `value / nominal − 1`, as a fraction (1e-4 = 100 ppm). `NaN` where there is no nominal. |
| `limitMin`, `limitMax` | The manual's limits for this value. `NaN` where it gives none. |

### Ratios (`quantity: "Ratio"`)

`value` is **V(100 mW) / V(range)** at the rear DC REFERENCE OUTPUT with FUNCTION in CALIBRATE: the quantity the
manual checks (Range Switch Performance Test, p. 7). There is one entry per range from 30 mW to 300 µW. 100 mW is
the reference, so it has no entry.

A negative `deviation` means V(range) is high relative to V(100 mW), and a positive one means it is low. For example,
`deviation = -2.0e-4` means the range's output voltage is about 200 ppm higher, relative to the 100 mW output,
than the middle of the manual's limits.

How that maps onto a power meter's reading depends on the meter. That mapping is the power meter app's to apply;
this file reports only what was measured.

### Voltages (`quantity: "DcVoltage"`)

The DC REFERENCE OUTPUT voltage of each Part 1 range, 100 mW to 300 µW. Only 1 mW has limits: 143.00–147.00 mV,
so its nominal is 145.00 mV. For the others, use the ratios.

### Resistances (`quantity: "FourWireResistance"`)

The four-wire resistance at DC REFERENCE OUTPUT with FUNCTION in STANDBY, 300 µW to 3 µW (manual p. 8), against the
manual's limits.

## Example (abridged)

```json
{
  "format": "11683A-actuals/1",
  "unit": { "model": "11683A", "serial": "SG52060005", "options": "H01" },
  "measured": "2026-10-17T14:30:05-07:00",
  "source": "11683A_SG52060005_20261017-1430.json",
  "sourceSha256": "4f1c…",
  "coverageFactor": 2,
  "simulated": false,
  "ratios": [
    { "range": "10mW", "nominalWatts": 0.01, "quantity": "Ratio", "unit": "",
      "value": 10.79235, "expandedUncertainty": 0.00135, "nominal": 10.7915,
      "deviation": 7.9e-5, "limitMin": 10.768, "limitMax": 10.815 }
  ],
  "voltages": [
    { "range": "1mW", "nominalWatts": 0.001, "quantity": "DcVoltage", "unit": "V",
      "value": 0.1450012, "expandedUncertainty": 1.48e-5, "nominal": 0.145,
      "deviation": 8.3e-6, "limitMin": 0.143, "limitMax": 0.147 }
  ],
  "resistances": [ … ]
}
```
