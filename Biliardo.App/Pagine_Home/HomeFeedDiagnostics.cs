using Biliardo.App.Servizi_Diagnostics;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Biliardo.App.Pagine_Home
{
    internal enum HomeFeedDiagReason
    {
        None,
        RefreshInProgress,
        LoadingMore,
        Offline,
        Scrolling,
        NoMorePosts,
        LoadInProgress,
        CancelledOrTimeout,
        NetworkNotIdleOrOffline,
        NotIdleOrOffline,
        BecameBusyOrOffline,
        EmptyPage,
        EmptyDelta,
        Error,
        Completed
    }

    internal static class HomeFeedDiagReasonExtensions
    {
        public static string ToDiagValue(this HomeFeedDiagReason reason) => reason switch
        {
            HomeFeedDiagReason.RefreshInProgress => "refresh_in_progress",
            HomeFeedDiagReason.LoadingMore => "loading_more",
            HomeFeedDiagReason.Offline => "offline",
            HomeFeedDiagReason.Scrolling => "scrolling",
            HomeFeedDiagReason.NoMorePosts => "no_more_posts",
            HomeFeedDiagReason.LoadInProgress => "load_in_progress",
            HomeFeedDiagReason.CancelledOrTimeout => "cancelled_or_timeout",
            HomeFeedDiagReason.NetworkNotIdleOrOffline => "network_not_idle_or_offline",
            HomeFeedDiagReason.NotIdleOrOffline => "not_idle_or_offline",
            HomeFeedDiagReason.BecameBusyOrOffline => "became_busy_or_offline",
            HomeFeedDiagReason.EmptyPage => "empty_page",
            HomeFeedDiagReason.EmptyDelta => "empty_delta",
            HomeFeedDiagReason.Error => "error",
            HomeFeedDiagReason.Completed => "completed",
            _ => "none"
        };
    }

    internal sealed class HomeFeedDiagScope : IDisposable
    {
        private readonly string _operation;
        private readonly Stopwatch _sw;
        private readonly Dictionary<string, int> _cardinality = new(StringComparer.Ordinal);
        private bool _completed;

        private HomeFeedDiagScope(string operation, HomeFeedDiagReason startReason)
        {
            _operation = operation;
            _sw = Stopwatch.StartNew();
            DiagLog.Note($"Home.Feed.{operation}.Start", startReason.ToDiagValue());
        }

        public static HomeFeedDiagScope Start(string operation, HomeFeedDiagReason startReason = HomeFeedDiagReason.None)
            => new(operation, startReason);

        public void SetCardinality(string name, int value)
        {
            _cardinality[name] = value;
            DiagLog.Note($"Home.Feed.{_operation}.{name}", value.ToString());
        }

        public void End(HomeFeedDiagReason endReason = HomeFeedDiagReason.Completed)
        {
            if (_completed)
                return;

            _completed = true;
            _sw.Stop();
            DiagLog.Note($"Home.Feed.{_operation}.elapsed_ms", _sw.ElapsedMilliseconds.ToString());
            DiagLog.Note($"Home.Feed.{_operation}.end_reason", endReason.ToDiagValue());
        }

        public void Dispose() => End();
    }
}
