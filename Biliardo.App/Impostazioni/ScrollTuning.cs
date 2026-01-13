using System;

namespace Biliardo.App.Impostazioni
{
    public static class ScrollTuning
    {
        /// <summary>
        /// 10 = default, 1 = poco attrito (scroll più lungo)
        /// </summary>
        public const int RallentamentoScroll = 10;

        public static float GetAndroidFlingScale()
        {
            var clamped = Math.Clamp(RallentamentoScroll, 1, 20);
            var scale = 10f / clamped;
            return Math.Clamp(scale, 0.6f, 2.2f);
        }

        public static double GetWindowsInertiaScale()
        {
            var clamped = Math.Clamp(RallentamentoScroll, 1, 20);
            var scale = 10.0 / clamped;
            return Math.Clamp(scale, 0.85, 1.35);
        }
    }
}
