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
| 2. Range auto-detection, Part 1 (volts/ratios), Part 2 (4-wire ohms) | #5–#7 | Done; bench checks below |
| 3. Uncertainty/TUR, console report, JSON, PDF, export | #8–#12 | Done; bench checks below |
| 4. Part 3 supply-adjustment assist, Part 4 H01 REF EXT check | #13–#15 | Done; bench checks below |

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
11683A-Verify part4                     # Part 4: Option H01 REF EXT check (needs a power meter)
11683A-Verify meter-test --meter 437B   # power meter performance test (435A/B, 436A, 437B, 438A, E4418B, E4419B)
11683A-Verify adjust                    # Part 3: Power Supply Adjustment assist (A2R1), then Part 1
11683A-Verify wiring                    # wiring diagram and 4-wire cable rules
11683A-Verify --regen-pdf results\11683A_SG52060005_20261017-1430.json   # rebuild a PDF report
11683A-Verify --print results\11683A_SG52060005_20261017-1430.json       # print a saved report
11683A-Verify dvm-check --simulate      # no hardware; also --simulate=supply-low, ratio-fail,
                                        # ohms-fail, noisy, drifting, rear-terminals
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
 │               ├────┼──── 4-wire cable ────┼─► DC REFERENCE OUTPUT ◎       │
 │  LO        ───┤    │     ≤ 5 ft, pairs    │      centre: HI + HI Sense    │
 │  LO Sense  ───┘    │     joined at the    │      shell:  LO + LO Sense    │
 └────────────────────┘     BNC plug         │   100 MEG LOAD MIN            │
                                             └───────────────────────────────┘
```

- **The DVM must read its FRONT terminals.** The 34401A's Front/Rear button (the 3458A's Terminals button)
  can't be set over the bus. Before the first measurement every command reads the switch (`ROUT:TERM?` on the
  34401A, `TERM?` on the 3458A). If it is on REAR, the app asks you to press the button and waits until FRONT
  is selected. On REAR the open input floats at some millivolts, and the ohms safety check refuses ohms.
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

## Running a test

`verify`, `part1` and `part2` start by showing the wiring diagram.

- **One screen per step.** At a console, each step has its own cleared screen: Setup, Wiring, Warm-up, each
  part's set-up and measuring, its results, then Results. The top of every screen shows the unit and DVM, and
  a step tracker with the current step highlighted, finished steps ticked and skipped ones struck through.
  A results screen waits for Enter before the next step clears it. The run uses the terminal's alternate
  screen, like a full-screen program. When it ends, the normal screen comes back with the results tables,
  the summary and where everything was saved, so they stay in the scrollback. `dvm-check`, `adjust`,
  `part4` and `meter-test` work the same way. Redirected or scripted runs scroll as before, with a heading
  for each step. The session log has everything, whatever was on screen.
- **Ambient temperature.** The app asks for it, unless `--ambient` was given. Outside 18–28 °C, the DVM's
  temperature coefficient is added.
- **Warm-up.** The app asks how long the 11683A and the DVM have been on, then counts down whatever is
  left. The 11683A needs 30 min (`warmup.calibratorMinutes`). The DVM's warm-up comes from its spec table:
  1 hour for the 34401A, 4 hours for the 3458A. Press **S** to skip the countdown; the report then says the
  warm-up was cut short. `--skip-warmup` leaves the warm-up out entirely.
- **Live table.** Each part shows a table of every range that is updated while a point is measured. The
  range being measured is marked ▶, and a status line shows the phase and the latest reading. Verdicts are
  colour-coded: PASS green, FAIL red, INDETERMINATE yellow, INFO blue.
- **Summary.** At the end, a summary panel gives each part's verdict and counts, any TUR below 4:1, the
  conditions, any supply-adjustment recommendation and the path of the results file.
- **Printing.** After the summary, the app asks "Print the report?" (default No). It then lists every
  installed printer, with the Windows default first and marked, plus *Don't print*. The printout is the same
  report as the PDF. Each page is rendered at 300 dpi and printed at its own size, or shrunk to fit A4, so no
  PDF reader is needed. `meter-test` offers the same for its test record.

## Results files

Each `verify`, `part1` or `part2` run saves **`results\11683A_<serial>_<yyyyMMdd-HHmm>.json`**. A copy of the
session log, which holds every bus exchange, is saved beside it with the same name. A second run in the
same minute gets `-2`. The JSON holds everything the report needs, so a report can be rebuilt from it alone:

- the unit, and the DVM: its identity reply, serial, firmware, calibration date and calibration state;
- the conditions (ambient, warm-up, and the simulation scenario if any) and every measurement and
  uncertainty setting;
- the limits file and the DVM spec table: each file's name, its stated source, and the SHA-256 of exactly
  what was loaded;
- for every point: each raw reading with its time, the DVM range, NPLC and input resistance, whether it
  settled, the uncertainty budget, the ratio, the TUR and the verdict;
- the Part 2 safety-check readings.

**PDF report.** A PDF report (`.pdf`, same name) is written beside the JSON. It contains:

- a title block in the style of the manual's record: model, serial, tested by, date, procedure, conditions;
- the test equipment and its traceability;
- the Part 1 and Part 2 tables, laid out like the manual's (Min. / Actual / Max.) and citing its pages,
  with σ, U, TUR and the verdicts;
- the uncertainty method, including the note on the 100 V range's 10 MΩ input;
- operator notes (asked for at the end of a run) and signature lines.

A simulated run is stamped "SIMULATED: not a calibration record". `--regen-pdf <json>` rebuilds the PDF
from a saved results file alone.

**Printing a saved report.** Use *Print a saved report* in the menu: it lists the newest 20 results files,
11683A and power meter runs alike. Or use `--print <json>`. At a console you choose the printer. Add
`--printer "<name>"` to name one, e.g. `--printer "Brother MFC-9340CDW Printer"`. Redirected, it uses the
Windows default printer. Each print is noted in the session log.

**Actuals for the power meter app.** After a passing run, the app offers to write
**`results\11683A-actuals.json`**. It holds the measured ratios, voltages and resistances, each with its U and
its deviation from the manual's nominal, so a power meter test can use the calibrator's actual output
instead of the nominal one. See [docs/actuals-format.md](docs/actuals-format.md).
`--export-actuals <json>` writes it from a saved results file.

The format is versioned (`"schema": "11683A-Verify/results/1"`), and a file with an unknown schema is refused.

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

## Power meter performance tests

`11683A-Verify meter-test`, or *Power meter performance test* in the menu, tests a power meter with the
11683A. Pick the meter, or name it with `--meter`. Its tests are loaded from **`data/meters/<model>.json`**:
one file per meter, transcribed from its own manual, with every row's 11683A setting, meter setting, expected
reading and min/max limits, and the page each one came from.

| Meter | Tests (from the manual) | Readings |
|---|---|---|
| HP 435A | Zero Carryover (power sensor), Instrumentation Accuracy | Bench DVM on the rear RECORDER OUTPUT, mVdc |
| HP 435B | Zero Carryover (power sensor), Instrumentation Accuracy, Calibration Factor | Bench DVM on the RECORDER OUTPUT, mVdc / Vdc |
| HP 436A | Zero Carryover, Instrument Accuracy (W, dBm, dB REL), Calibration Factor | HP-IB (`9A+T`) for watts; dBm/dB typed in |
| HP 437B | Zero Carryover, Instrument Accuracy | HP-IB (`LN`, `TR2`) |
| HP 438A | Zero Carryover, Instrument Accuracy, on channels A and B | HP-IB (`AP`/`BP`, `LN`, `TR2`); dBm/dB typed in |
| E4418B | Zero Test, Instrument Accuracy Procedure 1, Procedure 2 (H01 + DC calibrator) | SCPI (`UNIT1:POW?`, `FETC1?`) |
| E4419B | The E4418B's tests and limits, on channels A and B | SCPI (`UNIT2:POW?`, `FETC2? DEF,DEF,(@2)` for channel B) |

How a run goes:

1. Choose the tests. Procedure 2 is only offered on an H01 calibrator, and is off by default.
2. Enter the meter's serial number, and choose the channels on a 438A.
3. For each test, the app shows the manual's equipment and set-up steps.
4. For each row, it shows what to set on the 11683A and on the meter, then takes the reading:
   - over the bus, at `powerMeter.resource` or `--meter-resource`;
   - from the DVM, for the 435s, which the manual reads at the RECORDER OUTPUT;
   - typed in, for displays the bus can't read and for meters without a bus.
5. Each reading is checked against the manual's limits. Type S to skip a row or a test.
6. Results go to `results\<model>_<serial>_<stamp>.json`, with a PDF test record in the manual's
   Min / Actual / Max layout and the session log beside it.
7. Tests the 11683A can't do, such as the Power Reference Level tests (432A and thermistor mount), are
   listed on the record as not run.

Where editions or OCR disagree, each file's `ambiguities` list says which value was used and why. The PDF
repeats those notes. The format is described in [docs/meter-definitions.md](docs/meter-definitions.md),
so another meter can be added by writing one file.

In simulation (`meter-test --simulate --meter <model>`), a simulated meter follows the simulated 11683A. Readings
the simulator can't produce (dBm/dB displays) are entered by a "simulated operator" and marked **s**.

## Part 3: Power Supply Adjustment assist

`11683A-Verify adjust`, or *Adjust* in the menu. Use it when Part 1 recommends it, which happens when 1 mW
reads outside 143.00–147.00 mV. It follows the manual's Power Supply Adjustment (para 43–47, p. 8,
PDF p. 19):

1. It shows the manual's warning first: *"The operations in this section require the instrument is operated
   with its covers removed and should only be carried out by qualified service personnel."*
2. Set RANGE 1 mW, FUNCTION CALIBRATE, POLARITY NORMAL and REF INT, with the DVM on DC REFERENCE OUTPUT.
   Remove the top cover.
3. The 1 mW output is shown in large digits at NPLC 10, about 2.5 readings a second. Under it is a gauge
   from 142 to 148 mV, with the 143.00 and 147.00 mV limits and the 145.00 mV target, plus the error in mV
   and ppm and which way to turn. **Adjust A2R1 for the centre:** the display turns green within ±0.2 mV.
   Press Enter when done.
4. It then offers to rerun Part 1. Refit the cover and let the unit settle first. The Part 1 results are
   saved with a note of where A2R1 was left.

In simulation (`adjust --simulate=supply-low`), ↑/↓ turn the simulated A2R1 in coarse steps (0.1%) and
←/→ in fine steps (0.01%).

## Part 4: Option H01 REF EXT transfer check

`11683A-Verify part4`, or `verify --part4`. On an H01 unit, `verify` also asks at the end. The check is
informational: it compares power meter readings, so the result is an indication under the power meter's own
uncertainty and never changes the verdict. It follows the E4418B Service Guide's Instrument Accuracy Test
Procedure 2 (pp. 2-11 to 2-14, PDF pp. 51–54), and Table 2-4 is stored in
[`data/h01-ext-table.json`](data/h01-ext-table.json).

You need:

- a power meter on the 11683A's front-panel POWER METER output, connected with an 11730A cable;
- a DC calibrator whose Guard and LO can float;
- a BNC tee.

Set `powerMeter.model` to `E4418B`, `E4419B`, `437B` or `438A` to read the meter over the bus
(`powerMeter.resource`, default `GPIB0::13::INSTR`). Leave it as `manual` to type the readings in.

1. **1 mW voltage.** The app needs the 1 mW DC REFERENCE OUTPUT voltage. It takes it from Part 1 in the same
   session, or measures it first.
2. **Step 1, REF INT.** Connect the power meter with the 11730A cable. Set RANGE 1 mW. In STANDBY, wait for
   the meter's drift to stop and **zero** it. In CALIBRATE, **CAL** it. The app then reads the meter
   (P_INT).
3. **Step 2, REF EXT.** The app first warns about the shock risk from the H01 chassis unless the power meter is
   connected. **Unplug the 11683A's power cord**: the H01 has a ground loop.
   - Set RANGE **100 mW** and REF **EXT**, as the Service Guide's step 8 does.
   - Move the DVM to a **BNC tee on REF EXT**, and connect the DC calibrator to the tee.
   - Set the calibrator to the 1 mW voltage, to within ±20 ppm. The DVM shows the error live.
   - The app reads the meter (P_EXT). It reports P_EXT/P_INT − 1, corrected for the calibrator's small offset.
     The default tolerance is ±0.2%. Table 2-4 relates 144.93 mV to 1 mW, so about +0.05% is expected at
     145.00 mV.
4. **Step 3, optional: Table 2-4.** These are the Service Guide's steps 9–14.
   - Zero the meter in STANDBY with the calibrator at 0 V.
   - CAL it at 144.93 mV.
   - Set each of the ten voltages, from 458.31 µV to 15.588 V, with the meter filter the table gives. Each
     reading is compared with the table's own limits.
   - The rows are data and are **never scaled**: above 1 mW the table is not linear (15.588 V is 100 mW).
5. Turn the calibrator off, set REF back to INT, plug the power cord back in, and move the DVM back.

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

## Simulation

Every command runs without hardware with `--simulate[=scenario]`.

**Walkthrough.** Run the simulator at a console, for example
`dotnet run --project srcN83A-Verify -- --simulate`, and you get the bench UI exactly as it will be:
- **Every prompt is shown**, such as "Set RANGE to …", "Press Enter when FUNCTION is in STANDBY" or "zero
  the meter". Answering it turns the simulated 11683A's knobs.
- **Where the bench lets you choose**, such as which RANGE to measure next in Parts 1 and 2, a simulator
  list lets you pick the position. You can pick a wrong one, or change FUNCTION, POLARITY or REF, to see
  the warnings and the "try again" path.
- **Typed meter readings** offer the simulated display's value; press Enter to accept it or type another.
- **The menu comes back** after each command until you choose Exit. This applies on the bench too.

Scripted (redirected) runs still go straight through, setting the knobs themselves. The simulator provides a simulated 11683A
(knobs, settling, noise, drift, A2R1, REF EXT), a simulated 34401A that measures it with the real reading
times, and, for Part 4, a simulated power meter with zero and CAL. The app turns the simulated knobs itself.
Every result is marked SIMULATED in the console, the JSON and the PDF, and an actuals export from a simulated
run is flagged so it can't be used.

| Scenario | What it does | Try it with |
|---|---|---|
| `nominal` | Every range at the centre of its limits; 1 ppm noise. | `verify --simulate --part4` |
| `supply-low` | Supply 2% low: 1 mW about 142.1 mV, so A2R1 needs adjusting. The ratios still pass. | `verify`, then `adjust --simulate=supply-low` |
| `ratio-fail` | 10 mW 0.6% high: its ratio fails. | `part1 --simulate=ratio-fail` |
| `ohms-fail` | 3 µW 0.4% high: Part 2 fails. | `part2 --simulate=ohms-fail` |
| `noisy` | 30 ppm noise: slow to settle, wider σ. | `part1 --simulate=noisy` |
| `h01-ext-error` | REF EXT path 0.4% high: Part 4's agreement is outside 0.2%. | `part4 --simulate=h01-ext-error` |
| `drifting` | Outputs drift 15 ppm/min, as if not warmed up. | `part1 --simulate=drifting` |
| `rear-terminals` | The DVM's Front/Rear switch starts on REAR: the app asks for FRONT before measuring. | `dvm-check --simulate=rear-terminals` |

- **Part 3:** in `adjust --simulate=...`, ↑/↓ turn the simulated A2R1 in coarse steps and ←/→ in fine steps.
  The live view needs a real console.
- **Part 4:** the simulated power meter zeroes and CALs itself at the right moments. The simulated DC
  calibrator lands 3 ppm off each target, so the offset correction is exercised. The DVM is moved to the
  REF EXT tee and back.

## Step 1: what to try on the bench

1. Create `appsettings.local.json` with the 34401A's GPIB address, serial and calibration date.
2. Warm the 34401A up for at least an hour; its specifications assume 1 hour.
3. Connect the 4-wire cable from the 34401A's **FRONT** terminals to DC REFERENCE OUTPUT, and set FUNCTION
   to **STANDBY**. Press the 34401A's front-panel **Front/Rear** button so FRONT is selected: the "Rear"
   annunciator must be off. The switch can't be set over the bus.
4. Run `11683A-Verify dvm-check`. You should see:
   - the `*IDN?` string;
   - "The 34401A is reading its FRONT terminals". If it says REAR, press the Front/Rear button, then Enter.
     On REAR the open input reads some millivolts (about 11 mV on the bench), so the ohms ranges are refused;
   - the output reading a few µV ("grounded");
   - all seven ranges **OK**: DCV 100 mV, 1 V, 10 V and 100 V, and Ω 100, 1 k and 10 k;
   - `INP:IMP:AUTO? 1` on every DCV range up to 10 V and `0` on 100 V, and `ROUT:TERM? FRON` on every range.
5. When asked, set RANGE 1 mW, FUNCTION CALIBRATE, POLARITY NORMAL and REF INT. Accept the readings.
   Expect about 145 mV, inside 143.00–147.00 mV, with σ of a few µV. The session log records the result as a
   "1 mW result:" line with the mean, σ, n, DVM spec, U, limits and verdict.
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
6. Send back the session log (`results\logs\verify_*.log`) and a screenshot or copy of the two results
   tables.

## Steps 3 and 4: what to try on the bench

**Part 3, the Power Supply Adjustment assist.** This is a covers-off adjustment. Do it only if Part 1
recommends it.

1. Run `11683A-Verify adjust`. Accept the warning, then set RANGE 1 mW and FUNCTION CALIBRATE, with the DVM on
   DC REFERENCE OUTPUT.
2. Check that the large digits follow the output at about 2.5 readings a second, and that the gauge pointer
   moves as A2R1 is turned. It turns green within ±0.2 mV of 145.00 mV.
3. Press Enter. Accept the Part 1 rerun after refitting the cover. The 1 mW voltage should now read near
   145.00 mV.

Even without adjusting anything, it's worth running `adjust` once on the bench just to watch the live view:
decline the adjustment by pressing Enter straight away, then decline the rerun.

**Part 4, the Option H01 check.** It needs a power meter and an 11730A cable.

1. In `appsettings.local.json`, set `powerMeter.model` (`E4418B`, `437B`, `438A` or `manual`) and
   `powerMeter.resource`.
2. Run `11683A-Verify part4` and follow the prompts. The procedure is in the Part 4 section above.
3. With a bus meter, check the session log: the identity reply and the reading commands. These drivers
   have not run against a real meter yet.
4. Check the EXT/INT agreement. About +0.05% is expected at 145.00 mV.
5. Optionally run the Table 2-4 rows. Each should be within the table's limits.

Send back the session logs (`results\*.log`) and the PDFs.
