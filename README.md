# 11683A-Verify

Performance verification of an HP/Agilent **11683A Range Calibrator** (including Option H01) with an
**HP 34401A**, or an **HP 3458A**, over GPIB.

The 11683A has no bus. The operator turns its knobs and the app does the rest:

- it detects which range is selected and waits for the reading to settle;
- it averages the readings, computes the ratios and checks them against the manual's limits;
- it works out the measurement uncertainty and the TUR, and writes console and PDF reports.

C# / .NET 10 (x64), NI-VISA, Spectre.Console, QuestPDF (step 3). MIT licence.

## Status

| Step | Issues | State |
|---|---|---|
| 1. DVM drivers, spec tables, simulation | #1–#4 | Merged; bench check still to do |
| 2. Range auto-detection, Part 1 (volts/ratios), Part 2 (4-wire ohms) | #5–#7 | **Ready for the bench** |
| 3. Uncertainty/TUR, console report, JSON, PDF, export | #8–#12 | In progress: #8 done |
| 4. Part 3 supply-adjustment assist, Part 4 H01 REF EXT check | #13–#15 | Not started |

## Build and run

```
dotnet build 11683A-Verify.slnx
dotnet test 11683A-Verify.slnx
cd src\11683A-Verify\bin\Debug\net10.0
11683A-Verify dvm-check                 # the step-1 bench check
11683A-Verify spec-table                # DVM spec table, and the limit and TUR at every test point
11683A-Verify verify                    # Part 1 then Part 2 (the Range Switch Performance Test)
11683A-Verify part1                     # Part 1 only: DC voltage and ratio test
11683A-Verify part2                     # Part 2 only: four-wire resistance test
11683A-Verify dvm-check --simulate      # no hardware; also --simulate=supply-low, ratio-fail,
                                        # ohms-fail, noisy, drifting
11683A-Verify --help
```

**NI-VISA.** NI-VISA must be installed with .NET support. The app uses the IVI `IviFoundation.Visa` 8.0.2
package. Its `GlobalResourceManager` loads NI's modern-.NET implementation from
`C:\Program Files\IVI Foundation\VISA\Microsoft.NET\VendorAssemblies\ni`, which is 64-bit, so the app
builds as x64.

**The GPIB extender.** The bus runs through an HP 37204A HP-IB extender, so the default timeout is a
generous 20 s. The app talks only to the configured address.

## Configuration

`appsettings.json` holds the shared defaults: company, 11683A serial and options, DVM model and address,
NPLC, and readings per point.

Put bench-specific values in **`appsettings.local.json`** next to it. That file overrides the defaults and
git ignores it:

```json
{
  "operator": "Tony Goodhew",
  "dvm": {
    "resource": "GPIB0::22::INSTR",
    "serial": "3146A12345",
    "calibrationDate": "2026-03-14"
  }
}
```

The 34401A reports its serial as `0` over the bus, and the 3458A doesn't report one, so the report takes
the serial from `dvm.serial`.

## Wiring

```
 34401A front terminals                      11683A rear panel
 ┌────────────────────┐                      ┌───────────────────────────────┐
 │  HI        ───┐    │                      │  REF EXT ◎   (H01 input,      │
 │  HI Sense  ───┤    │                      │               Part 4 only)    │
 │               ├────┼──── 4-wire cable ────┼─► DC REFERENCE OUTPUT ◎        │
 │  LO        ───┤    │     ≤ 5 ft, pairs    │      centre: HI + HI Sense    │
 │  LO Sense  ───┘    │     joined at the    │      shell:  LO + LO Sense    │
 └────────────────────┘     BNC plug         │   100 MEG LOAD MIN            │
                                             └───────────────────────────────┘
```

- **Measure at DC REFERENCE OUTPUT**, the lower BNC. The upper BNC, **REF EXT**, is the Option H01
  reference *input*. It is used only in Part 4.
- **The 4-wire cable must go directly to the 11683A BNC.** Make the Kelvin split at the BNC plug. Use no
  adapters or tees: their contact resistance would be measured with the 11683A. The cable must be no more
  than 5 ft long (11683A manual, Table 2 and the note on p. 7).
- **The output needs a load of at least 100 MΩ** (manual p. 5; the rear panel says "100 MEG LOAD MIN").
  - The 34401A meets this on its 0.1, 1 and 10 V ranges with `INP:IMP:AUTO ON` (>10 GΩ). `CONF`
    switches that setting off, so the driver sends it again after every configure and reads it back.
  - The 100 V range, used only for the 100 mW reading of about 15.8 V, is fixed at 10 MΩ. The manual's own
    HP 3455A was also 10 MΩ on that range. The report notes this.
- **Ohms current is never applied while FUNCTION is in CALIBRATE.** Before any ohms range is configured,
  and again before every point in Part 2, the app reads the output on the 100 mV DC range. It applies ohms
  current only if the output is grounded, within 50 µV of zero, as it is in STANDBY. It allows up to 4 s
  for the output to fall to ground after the switch moves. If FUNCTION is moved to CALIBRATE partway through
  a point, the next reading no longer matches. The point is then abandoned and the check refuses to go on.
  After Part 2, the DVM is left on DC volts.

## What the app tests

Source: HP 11683A Operating and Service Manual (`11683A.pdf`), Range Switch Performance Test, manual
pp. 7–8 (PDF pp. 18–19). All limits are in [`data/limits-11683A.json`](data/limits-11683A.json).

| Part | Settings | What is measured | Limits |
|---|---|---|---|
| 1 | INT, NORMAL, CALIBRATE | DC volts from 100 mW down to 300 µW, then the ratio V(100 mW)/V(range) | The ratio for each range; 1 mW must also read 143.00–147.00 mV |
| 2 | STANDBY | 4-wire ohms from 300 µW down to 3 µW | 3143.3–3157.1 Ω, … , 31.580–31.718 Ω |
| 3 | 1 mW, CALIBRATE, covers off | Adjust A2R1 for 145.00 ± 2.00 mV | Adjustment, optional |
| 4 | REF EXT, LINE unplugged | Transfer check against a power meter (E4418B Service Guide, Procedure 2) | Informational |

## How a point is measured

The operator sets the knobs; the app works out which range is selected.

1. **Detect.** Quick scout readings at NPLC 1 start on the highest DVM range the test needs and step down until the
   reading is on scale. Once they have agreed to 1% for 2 s, the reading is matched to the nearest expected value:
   a Part 1 voltage or a Part 2 resistance. Within ±10% counts as a match. The ranges are √10 steps apart, so a
   range that fails its limits is still recognised. Anything else is a **warning, never a result**:
   - about 0 V: FUNCTION is in STANDBY, or REF is on EXT;
   - a negative reading: POLARITY is REVERSE (in ohms: the output is live);
   - not close to any expected value: check RANGE (in ohms: also check FUNCTION is in STANDBY);
   - overload in ohms: open circuit, so check the cable.
2. **Range.** The DVM goes to the fixed range for the detected value. It re-detects if a reading lands near either
   end of that range.
3. **Settle.** Every reading over the last 10 s, and at least three of them, must be within ±20 ppm of their
   mean. The band has floors of 2 µV and 2 mΩ. A point that hasn't settled after 2 minutes is reported, and the
   operator can measure it anyway; it is then flagged as not settled. These defaults are in `measurement` in
   `appsettings.json`.
4. **Measure.** N readings are taken (`readingsPerPoint`). If one leaves the band around the readings just
   before it, the point is abandoned and detected again: a knob was turned. The band widens to 5σ for a point
   measured unsettled, so noise alone can't abandon it.

**FET Balance Adjustment (not implemented).** The manual's FET Balance Adjustment (para 48–51, p. 8) is
needed only after the U1 assembly is replaced, or after the A3A1–A3J1 wires have moved. It needs an
oscilloscope and the A3 assembly removed, so the app doesn't cover it. Follow the manual.

## Uncertainty convention

Each DVM's accuracy table is stored as data in [`data/dvm-specs-34401A.json`](data/dvm-specs-34401A.json)
and [`data/dvm-specs-3458A.json`](data/dvm-specs-3458A.json). Each file cites its manual page.
`11683A-Verify spec-table` prints the table as loaded, so it can be checked against the page.

Uncertainty for each point:

- The spec limit (± reading term + range term, for the configured period, 1 year by default) is treated as
  a **rectangular** distribution: u_spec = spec / √3.
- If the ambient temperature is outside 18–28 °C, the temperature coefficient is added for each degree
  outside.
- The Type A component is σ / √N.
- These combine as **u_c = √(u_spec² + (σ/√N)²)**, expanded with **k = 2**: U = 2·u_c ≈ 1.155 × spec,
  plus the noise term.
- For ratios, the two readings' relative uncertainties combine by root-sum-square (default). Linear
  (worst-case) combination is optional.
- **TUR** = (half the tolerance band) / U. Any point below 4:1 is flagged.
- **Guard band:**
  - PASS: value ± U is entirely inside the limits.
  - FAIL: value ± U is entirely outside them.
  - Otherwise INDETERMINATE.
  - The report also shows simple acceptance (inside the limits or not, ignoring U) alongside each verdict.
- k, the ratio combination and the TUR threshold are under `uncertainty` in `appsettings.json`.
- `11683A-Verify spec-table` shows the TUR the DVM's spec alone allows at every test point. With a 34401A
  on its 1-year spec, the lowest is about 8:1, at 3 µW in Part 2. Every Part 1 ratio is 15:1 or better.

For the 3458A, the spec table includes two footnote adders: the "no MATH NULL, fixed range < 4 min"
adder and Keysight's factory traceability adder. Both are marked as applied in the data file. The
90-day, 1-year and 2-year columns also assume an ACAL within 24 hours and ±1 °C, so run `ACAL DCV` and
`ACAL OHMS` before testing.

## Option H01

The rear panel of SG52060005 has:

- an **INT/EXT** toggle;
- a **REF EXT** BNC, the external DC reference input;
- the separate **DC REFERENCE OUTPUT** BNC.

The 11683A manual doesn't document H01. The app's handling follows the E4418B Service Guide, Instrument
Accuracy Test Procedure 2 (pp. 2-11 to 2-14, PDF pp. 51–54). For the details, see
[docs/manual-notes.md](docs/manual-notes.md).

## Report library licence

The PDF report (step 3) uses QuestPDF under its **Community** licence. That licence is free for
individuals and for organisations with less than US $1M in annual gross revenue.

## Step 1: what to try on the bench

1. Create `appsettings.local.json` with the 34401A's GPIB address, serial and calibration date.
2. Warm the 34401A up for at least an hour; its specifications assume 1 hour.
3. Connect the 4-wire cable to DC REFERENCE OUTPUT and set FUNCTION to **STANDBY**.
4. Run `11683A-Verify dvm-check`. You should see:
   - the `*IDN?` string;
   - the output reading a few µV ("grounded");
   - all seven ranges **OK**: DCV 100 mV, 1 V, 10 V and 100 V, and Ω 100, 1 k and 10 k;
   - `INP:IMP:AUTO? 1` on every DCV range up to 10 V, and `0` on 100 V.
5. When asked, set RANGE 1 mW, FUNCTION CALIBRATE, POLARITY NORMAL and REF INT. Accept the readings.
   Expect about 145 mV, inside 143.00–147.00 mV, with σ of a few µV.
6. Run `11683A-Verify spec-table`. Check the 34401A table against Service Guide p. 12 (PDF p. 20).
7. Send back the session log, from `results\logs\` under the current directory.

## Step 2: what to try on the bench

Do the Step 1 check above first, if you haven't already.

1. Warm up the 34401A for at least an hour, and the 11683A for at least 30 minutes.
2. Connect the 4-wire cable to DC REFERENCE OUTPUT.
3. Run `11683A-Verify verify` (or `part1`, then `part2`). Pass `--ambient <°C>` if the room is outside 18–28 °C.
4. **Part 1.** Set REF INT, POLARITY NORMAL and FUNCTION CALIBRATE, then press Enter.
   - At each prompt, turn RANGE to any untested position from 100 mW down to 300 µW and press Enter.
   - The app detects the range, waits for it to settle (about 20–30 s), then takes 10 readings at NPLC 100
     (about 35 s).
   - Try turning a knob partway through one point. The point should be abandoned and detected again.
   - Try POLARITY REVERSE, or FUNCTION STANDBY, at a prompt. You should get a warning, not a result.
5. **Part 2.** Set FUNCTION to STANDBY and press Enter. Then turn RANGE through 300 µW down to 3 µW.
   - Before each point, the log shows an "Ohms safety check" line of a few µV.
   - Try leaving FUNCTION in CALIBRATE at the start. Ohms must be refused, and the log must show no
     `CONF:FRES` command.
6. Send back the session log (`results\logserify_*.log`) and a screenshot or copy of the two results
   tables.
