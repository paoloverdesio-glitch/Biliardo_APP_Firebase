# Build, smoke test e telemetria feed Home

## 1) Build su macchina con workload MAUI completi

Eseguire i build su host Windows con workload MAUI Android + Windows installati:

```bash
dotnet build Biliardo.App/Biliardo.App.csproj -f net8.0-android -c Release
dotnet build Biliardo.App/Biliardo.App.csproj -f net8.0-windows10.0.19041.0 -c Release
```

## 2) Smoke test scriptato (sequenza obbligatoria)

Per una sessione valida, usare questa sequenza in Home:

1. Apertura Home (attesa caricamento iniziale).
2. Long scroll verso il basso (almeno 60s).
3. Pull-to-refresh in cima alla lista.
4. Scroll nuovamente verso il basso fino a trigger prefetch older.
5. Apertura composer + invio bozza/post test.
6. Apertura media allegato (immagine/video/pdf) e ritorno.

Al termine uscire dalla Home per forzare flush telemetria `Home.FeedMetrics.*` in `DiagLog`.

## 3) Raccolta metriche e confronto baseline

1. Esportare due file diagnostici `.txt` (baseline e current).
2. Eseguire script:

```bash
python3 scripts/compare_feed_telemetry.py \
  --baseline /path/baseline.txt \
  --current /path/current.txt \
  --output /path/feed_telemetry_delta.csv
```

Il CSV contiene baseline/current/delta per:

- `Home.FeedMetrics.hitch_per_min`
- `Home.FeedMetrics.fetch_avg_ms`
- `Home.FeedMetrics.refresh_latest_avg_ms`
- `Home.FeedMetrics.apply_batch_avg_ms`
- `Home.FeedMetrics.max_scroll_gap_ms`

## 4) Criteri di accettazione numerici proposti

Accettare la build se tutte le condizioni sono vere:

- `hitch_per_min <= 12.0`
- `fetch_avg_ms <= 900`
- `refresh_latest_avg_ms <= 1200`
- `apply_batch_avg_ms <= 24`
- `max_scroll_gap_ms <= 450`
- Nessuna regressione > +20% rispetto alla baseline per ogni metrica.
