using Biliardo.App.Servizi_Diagnostics;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace Biliardo.App.Pagine_Home
{
    public partial class Pagina_Home
    {
        private const int FeedMetricsHitchThresholdMs = 180;

        private void ResetFeedMetricsSession()
        {
            lock (_feedMetricsLock)
            {
                _feedMetricsSessionStartUtc = DateTimeOffset.UtcNow;
                _feedMetricsLastScrollEventUtc = null;
                _feedMetricsScrollSamples = 0;
                _feedMetricsHitchCount = 0;
                _feedMetricsMaxScrollGapMs = 0;
                _feedMetricsFetchDurationMsSum = 0;
                _feedMetricsFetchCount = 0;
                _feedMetricsRefreshLatestDurationMsSum = 0;
                _feedMetricsRefreshLatestCount = 0;
                _feedMetricsApplyBatchDurationMsSum = 0;
                _feedMetricsApplyBatchCount = 0;
            }

            DiagLog.Note("Home.FeedMetrics.session_start_utc", _feedMetricsSessionStartUtc.ToString("o", CultureInfo.InvariantCulture));
        }

        private void RecordScrollGapSample()
        {
            var now = DateTimeOffset.UtcNow;

            lock (_feedMetricsLock)
            {
                if (_feedMetricsLastScrollEventUtc.HasValue)
                {
                    var gapMs = (long)Math.Max(0, (now - _feedMetricsLastScrollEventUtc.Value).TotalMilliseconds);
                    _feedMetricsScrollSamples++;

                    if (gapMs > _feedMetricsMaxScrollGapMs)
                        _feedMetricsMaxScrollGapMs = gapMs;

                    if (gapMs >= FeedMetricsHitchThresholdMs)
                        _feedMetricsHitchCount++;
                }

                _feedMetricsLastScrollEventUtc = now;
            }
        }

        private void RecordFetchDuration(long elapsedMs, bool isLatestRefresh)
        {
            if (elapsedMs < 0)
                return;

            lock (_feedMetricsLock)
            {
                _feedMetricsFetchDurationMsSum += elapsedMs;
                _feedMetricsFetchCount++;

                if (isLatestRefresh)
                {
                    _feedMetricsRefreshLatestDurationMsSum += elapsedMs;
                    _feedMetricsRefreshLatestCount++;
                }
            }
        }

        private void RecordApplyBatchDuration(long elapsedMs)
        {
            if (elapsedMs < 0)
                return;

            lock (_feedMetricsLock)
            {
                _feedMetricsApplyBatchDurationMsSum += elapsedMs;
                _feedMetricsApplyBatchCount++;
            }
        }

        private void FlushFeedMetricsSession()
        {
            DateTimeOffset startUtc;
            int scrollSamples;
            int hitchCount;
            long maxScrollGapMs;
            long fetchDurationMsSum;
            int fetchCount;
            long refreshLatestDurationMsSum;
            int refreshLatestCount;
            long applyBatchDurationMsSum;
            int applyBatchCount;

            lock (_feedMetricsLock)
            {
                startUtc = _feedMetricsSessionStartUtc;
                scrollSamples = _feedMetricsScrollSamples;
                hitchCount = _feedMetricsHitchCount;
                maxScrollGapMs = _feedMetricsMaxScrollGapMs;
                fetchDurationMsSum = _feedMetricsFetchDurationMsSum;
                fetchCount = _feedMetricsFetchCount;
                refreshLatestDurationMsSum = _feedMetricsRefreshLatestDurationMsSum;
                refreshLatestCount = _feedMetricsRefreshLatestCount;
                applyBatchDurationMsSum = _feedMetricsApplyBatchDurationMsSum;
                applyBatchCount = _feedMetricsApplyBatchCount;
            }

            var elapsedMinutes = Math.Max((DateTimeOffset.UtcNow - startUtc).TotalMinutes, 1d / 60d);
            var hitchPerMinute = hitchCount / elapsedMinutes;
            var fetchAvgMs = fetchCount > 0 ? fetchDurationMsSum / (double)fetchCount : 0d;
            var refreshLatestAvgMs = refreshLatestCount > 0 ? refreshLatestDurationMsSum / (double)refreshLatestCount : 0d;
            var applyBatchAvgMs = applyBatchCount > 0 ? applyBatchDurationMsSum / (double)applyBatchCount : 0d;

            DiagLog.Note("Home.FeedMetrics.elapsed_min", elapsedMinutes.ToString("F3", CultureInfo.InvariantCulture));
            DiagLog.Note("Home.FeedMetrics.scroll_samples", scrollSamples.ToString(CultureInfo.InvariantCulture));
            DiagLog.Note("Home.FeedMetrics.hitch_count", hitchCount.ToString(CultureInfo.InvariantCulture));
            DiagLog.Note("Home.FeedMetrics.hitch_per_min", hitchPerMinute.ToString("F3", CultureInfo.InvariantCulture));
            DiagLog.Note("Home.FeedMetrics.max_scroll_gap_ms", maxScrollGapMs.ToString(CultureInfo.InvariantCulture));

            DiagLog.Note("Home.FeedMetrics.fetch_count", fetchCount.ToString(CultureInfo.InvariantCulture));
            DiagLog.Note("Home.FeedMetrics.fetch_avg_ms", fetchAvgMs.ToString("F3", CultureInfo.InvariantCulture));

            DiagLog.Note("Home.FeedMetrics.refresh_latest_count", refreshLatestCount.ToString(CultureInfo.InvariantCulture));
            DiagLog.Note("Home.FeedMetrics.refresh_latest_avg_ms", refreshLatestAvgMs.ToString("F3", CultureInfo.InvariantCulture));

            DiagLog.Note("Home.FeedMetrics.apply_batch_count", applyBatchCount.ToString(CultureInfo.InvariantCulture));
            DiagLog.Note("Home.FeedMetrics.apply_batch_avg_ms", applyBatchAvgMs.ToString("F3", CultureInfo.InvariantCulture));
        }

        private async Task<T> MeasureElapsedAsync<T>(Func<Task<T>> action, Action<long> recorder)
        {
            var sw = Stopwatch.StartNew();
            var result = await action();
            sw.Stop();
            recorder(sw.ElapsedMilliseconds);
            return result;
        }

        private async Task MeasureElapsedAsync(Func<Task> action, Action<long> recorder)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            recorder(sw.ElapsedMilliseconds);
        }
    }
}
