#!/usr/bin/env python3
import argparse
import csv
import re
from pathlib import Path

METRICS_KEYS = [
    "Home.FeedMetrics.hitch_per_min",
    "Home.FeedMetrics.fetch_avg_ms",
    "Home.FeedMetrics.refresh_latest_avg_ms",
    "Home.FeedMetrics.apply_batch_avg_ms",
    "Home.FeedMetrics.max_scroll_gap_ms",
]

LINE_RE = re.compile(r"^\S+\s*\|\s*(Home\.FeedMetrics\.[\w_]+)\s*=\s*(.*?)\s*$")


def extract_metrics(path: Path):
    values = {}
    with path.open("r", encoding="utf-8", errors="ignore") as handle:
        for raw in handle:
            match = LINE_RE.match(raw.strip())
            if not match:
                continue
            key, value = match.group(1), match.group(2)
            try:
                values[key] = float(value)
            except ValueError:
                continue
    return values


def main():
    parser = argparse.ArgumentParser(description="Confronta baseline/current delle metriche feed Home.")
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--current", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=Path("feed_telemetry_delta.csv"))
    args = parser.parse_args()

    baseline = extract_metrics(args.baseline)
    current = extract_metrics(args.current)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="", encoding="utf-8") as csv_file:
        writer = csv.writer(csv_file)
        writer.writerow(["metric", "baseline", "current", "delta", "delta_pct"])

        for metric in METRICS_KEYS:
            b = baseline.get(metric, 0.0)
            c = current.get(metric, 0.0)
            delta = c - b
            delta_pct = (delta / b * 100.0) if b != 0 else 0.0
            writer.writerow([
                metric,
                f"{b:.3f}",
                f"{c:.3f}",
                f"{delta:.3f}",
                f"{delta_pct:.2f}",
            ])

    print(f"CSV generato: {args.output}")


if __name__ == "__main__":
    main()
