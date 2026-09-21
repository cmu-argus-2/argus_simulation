"""Export existing gyro experiment data into the four requested fields."""
from pathlib import Path
import csv
import math

root = Path(__file__).resolve().parent
source = root / 'experiments/results/gyro_comparison.csv'
with source.open() as stream:
    rows = [r for r in csv.DictReader(stream) if float(r['time_s']) > 0]
assert len(rows) == 6000
times = [float(r['time_s']) for r in rows]
assert all(abs(b-a-.1) < 1e-9 for a, b in zip(times, times[1:]))
out = root / 'experiments/results/gyro_streams'
out.mkdir(exist_ok=True)
for case in ['white', 'random_walk', 'truth']:
    path = out / f'gyro_{case}.csv'
    with path.open('w', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(['timestamp', 'gyroX', 'gyroY', 'gyroZ'])
        for row in rows:
            values = [float(row['time_s'])] + [float(row[f'{case}_w{axis}_rad_s']) for axis in 'xyz']
            assert all(math.isfinite(v) for v in values)
            writer.writerow(values)
    print(f'{path.name}: {len(rows)} rows, {times[0]}–{times[-1]} s, 10 Hz')
