# Validation — 2026-09-21

Executed all three sensor scripts and export_gyro.py with Python 3.12,
Basilisk 2.11.1, NumPy 2.4.6 and Matplotlib 3.10.9 on macOS.
Used an existing Basilisk environment; installation in a fresh environment was not tested.

- Ideal gyro: zero measurement-minus-truth error.
- White gyro X: mean error 0.001986 rad/s, standard deviation 0.001003 rad/s,
  consistent with the illustrative 0.002 bias and 0.001 noise setting.
- Random-walk X lag-one correlation: 0.999432; default transition output matched identity case in this run.
- Ideal accelerometer maximum error: 5.68e-13 m/s² in free fall and
  1.05e-12 m/s² with 1 N applied force on 10 kg.
- Mounting: zero forward/inverse error in both aligned and rotated cases.
- Export: three 6000-row streams, timestamps 0.1–600 s at 10 Hz,
  with finite values and four columns. Committed samples contain ten rows each.

The legacy orbit/Vizard example was not rerun in this validation.
No Unity playback, visual exporter integration, FSW ingestion or OD accuracy
evaluation is claimed by these results.
