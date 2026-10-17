# 11683A-Verify

Performance verification of an HP/Agilent **11683A Range Calibrator** (including Option H01) using an
**HP 34401A** (or **HP 3458A**) over GPIB.

The 11683A has no bus. The operator turns its knobs and the app does the rest: it detects the selected
range, waits for the reading to settle, averages it, computes the ratios, evaluates the manual's limits,
and works out the measurement uncertainty and TUR. It then writes console and PDF reports.

Work in progress. See the GitHub issues and milestones for the build plan.
