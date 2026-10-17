# Manual notes

What was checked against which page, and what it changed in the design. Page numbers are the manual's
own, with the PDF page in parentheses, from the local manual library.

## HP 11683A Operating and Service Manual (`11683A.pdf`)

| Item | Finding | Page |
|---|---|---|
| Part 1 ratio limits, 30 mW to 300 µW | Match the prompt. The OCR'd "300 mW" row is the 300 µW range. | p. 7 (PDF 18) |
| 1 mW voltage limits | 143.00–147.00 mVdc. Outside them, perform the Power Supply Adjustment. | p. 7 (PDF 18) |
| Procedure start | 11683A set to RANGE 100 mW, FUNCTION STANDBY, POLARITY NORMAL; DVM able to read up to +20 Vdc at 5-digit resolution. FUNCTION then goes to CALIBRATE for the voltages. | p. 7 (PDF 18) |
| 4-wire cable | Must connect directly to the 11683A, with no connectors or adapters. | p. 7 (PDF 18) |
| Part 2 ohms limits, 300 µW to 3 µW | All ten values match the prompt. FUNCTION in STANDBY. | p. 8 (PDF 19) |
| Power Supply Adjustment | RANGE 1 mW; adjust A2R1 for 145.00 ± 2.00 mVdc at the rear DC REFERENCE OUTPUT; top cover off. | p. 8 (PDF 19) |
| Covers-off warning | "The operations in this section require the instrument is operated with its covers removed and should only be carried out by qualified service personnel." | p. 8 (PDF 19) |
| FET Balance Adjustment | Needs an oscilloscope and A3 removed; only after U1 is replaced or the A3A1–A3J1 wires move. Not implemented. | p. 8 (PDF 19) |
| Output load | DC REFERENCE OUTPUT load must be ≥ 100 MΩ. | p. 5 (PDF 16) |
| FUNCTION switch | CALIBRATE couples the output to the power meter; STANDBY grounds the output. | p. 5 (PDF 16) |

## HP 34401A Service Guide (`34401A Service.pdf`) and User's Guide (`34401A User.pdf`)

| Item | Finding | Page |
|---|---|---|
| DC and resistance accuracy | Transcribed into `data/dvm-specs-34401A.json`. | Service p. 12 (PDF 20) |
| Warm-up | Specifications are for a 1-hour warm-up at 6½ digits. | Service p. 13 (PDF 21) |
| Input resistance | 0.1, 1 and 10 V ranges: selectable 10 MΩ or >10 GΩ. 100 V and 1000 V: 10 MΩ ±1%. Low ranges clamp above ±17 V through 100 kΩ. | Service p. 13 (PDF 21) |
| **`CONF` resets `INP:IMP:AUTO`** | "The CONFigure and MEASure? commands automatically turn AUTO OFF." The driver re-sends `INP:IMP:AUTO ON` after every `CONF` and reads it back. | User p. 53 (PDF 61) |
| SCPI | `CONF:VOLT:DC`, `VOLT:DC:NPLC`, `INP:IMP:AUTO`, `CONF:FRES`, `FRES:NPLC`, `ZERO:AUTO`, `READ?` and `SYST:ERR?` are all in the GPIB-MCP 34401A command database, taken from the User's Guide. | — |
| Serial | `*IDN?` reports serial `0`, so it comes from config. | — |

## HP 3458A User's Guide (`3458A Multimeter User's Guide .pdf`)

| Item | Finding | Page |
|---|---|---|
| DCV accuracy (ppm) and adders | Transcribed into `data/dvm-specs-3458A.json`. Adders: without MATH NULL and on a fixed range < 4 min, add 17, 1.7 and 0.25 ppm of range on 0.1, 1 and 10 V; add 2 ppm of reading for factory traceability. | p. 284 (PDF 286) |
| Ohms accuracy (ppm) | Specifications for PRESET, NPLC 100, OCOMP ON, OHMF. Add 3 ppm of reading for traceability. | p. 286 (PDF 288) |
| ACAL | The 90-day, 1-year and 2-year specifications require operation within 24 hours and ±1 °C of the last ACAL. | p. 284 (PDF 286) |
| Commands | `PRESET`, `FUNC`, `NPLC`, `AZERO`, `FIXEDZ`, `OCOMP`, `NDIG`, `TRIG`, `END`, `ID?`, `REV?` and `ERRSTR?` are in the GPIB-MCP 3458A command database. | — |

## E4418B Service Guide (`E4418B Service.pdf`): Option H01

| Item | Finding | Page |
|---|---|---|
| Procedure 2 setup | RANGE 100 mW, POLARITY NORMAL, FUNCTION STANDBY, **REF SWITCH: EXT**, LINE OFF. | p. 2-12 (PDF 52) |
| Power cord | Unplug it because of the ground loop. Warning: risk of mild shock from the chassis unless the power meter is connected. The DC calibrator's Guard and LO must float. | p. 2-12 (PDF 52) |
| Zero and CAL | Zero the power meter in STANDBY, after the drift settles (typically < 60 s). Then apply 144.93 mV and CAL. | p. 2-13 (PDF 53) |
| **Table 2-4: not linear above 1 mW** | 458.31 µV up to 144.93 mV is linear in power. Above that it is not: 459.12 mV ≙ 3.1623 mW (linear would be 458.31 mV), 1.4588 V ≙ 10 mW, 4.6872 V ≙ 31.62 mW, and 15.588 V ≙ 100 mW (linear would be 14.493 V). Store the table; never scale it. | p. 2-14 (PDF 54) |
| True full scale | The "3" ranges are √10 steps: 3.16 µW, 31.6 µW, 316 µW and so on. | p. 2-10 (PDF 50) |

## E4418B Service Guide: Procedure 2 in detail (Part 4)

| Item | Finding | Page |
|---|---|---|
| Equipment | 11683A Option H01, 11730A power sensor cable, a DC calibrator (Datron 4000A in the guide). | p. 2-11 (PDF 51) |
| RANGE in EXT | The 11683A stays at **RANGE 100 mW** with REF EXT: the DC calibrator's voltage sets the level, 144.93 mV ≙ 1 mW. Part 4 uses this for the EXT step. | p. 2-12 (PDF 52) |
| Zero | STANDBY, calibrator at 0 V with its output off, meter filter 512, then Zero; the display should read 0 ± 0.05 µW. | p. 2-13 (PDF 53) |
| Table 2-4 | All ten rows: setting, effective power, filter (256/64/16), measurement uncertainty, lower and upper limits. Stored in `data/h01-ext-table.json`. The rows are rounded: 458.31 µV is 3.16228 µW by linear scaling, but the table prints 3.1623 µW, so the app interpolates through the printed rows. "The test limits shown in this table do not take into account the corresponding measurement uncertainty values." | p. 2-14 (PDF 54) |

## Power meter commands (Part 4)

From the GPIB-MCP command database, which is taken from each meter's own manual. None of these has been run against
a real meter yet.

| Meter | Identity | Reading |
|---|---|---|
| E4418B/E4419B (SCPI) | `*IDN?` ("…,E4418B,serial,firmware") | `UNIT1:POW?` (W or DBM), then `FETC1?`; `SYST:ERR?` after. Nothing on the meter is changed. |
| 437B | `*IDN?` ("HEWLETT-PACKARD,437B,,X.X") | `LN` (watts), `TR2` (one settled reading, read back), `TR3` (free run again). |
| 438A | `?ID` ("HP438A,VERX.XX") | `AP` (sensor A), `LN`, `TR2`, `TR3`. |

## Decisions confirmed by the owner (2026-10-16)

- **Rear panel of SG52060005:** an INT/EXT toggle, the **REF EXT** BNC (upper), and the separate
  **DC REFERENCE OUTPUT** BNC (lower, "100 MEG LOAD MIN").
- **FUNCTION in Part 4:** follow the 11683A manual's definitions. STANDBY grounds the output, so zero the
  power meter there; CALIBRATE couples the output, so CAL and measure there.
- **Uncertainty:** spec treated as rectangular, k = 2. Documented in the README.
