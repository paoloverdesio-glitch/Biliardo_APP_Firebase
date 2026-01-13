using System;
using System.Runtime.CompilerServices;
using AndroidX.RecyclerView.Widget;
using Biliardo.App.Impostazioni;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI
{
    public static partial class ChatScrollTuning
    {
        private static readonly ConditionalWeakTable<RecyclerView, TuningFlingListener> Listeners = new();

        static partial void ApplyPlatform(CollectionView view)
        {
            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return;

            if (Listeners.TryGetValue(recycler, out _))
                return;

            var listener = new TuningFlingListener(recycler);
            Listeners.Add(recycler, listener);
            recycler.SetOnFlingListener(listener);

            if (recycler.ItemAnimator is SimpleItemAnimator simpleAnimator)
                simpleAnimator.SupportsChangeAnimations = false;
            else
                recycler.ItemAnimator = null;

            recycler.SetItemViewCacheSize(20);
            recycler.SetHasFixedSize(true);
        }

        private sealed class TuningFlingListener : RecyclerView.OnFlingListener
        {
            private readonly RecyclerView _recycler;
            private bool _handling;

            public TuningFlingListener(RecyclerView recycler)
            {
                _recycler = recycler;
            }

            public override bool OnFling(int velocityX, int velocityY)
            {
                if (_handling)
                    return false;

                var scale = ScrollTuning.GetAndroidFlingScale();
                var scaledX = (int)Math.Clamp(velocityX * (double)scale, -MaxVelocity, MaxVelocity);
                var scaledY = (int)Math.Clamp(velocityY * (double)scale, -MaxVelocity, MaxVelocity);

                _handling = true;
                try
                {
                    _recycler.SetOnFlingListener(null);
                    _recycler.Fling(scaledX, scaledY);
                }
                catch
                {
                    return false;
                }
                finally
                {
                    _recycler.SetOnFlingListener(this);
                    _handling = false;
                }

                return true;
            }

            private const int MaxVelocity = 12000;
        }
    }
}
