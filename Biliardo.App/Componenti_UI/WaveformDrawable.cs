using System;
using Microsoft.Maui.Graphics;

namespace Biliardo.App.Componenti_UI
{
    public sealed class WaveformDrawable : IDrawable
    {
        private readonly int _maxSamples;
        private readonly float[] _samples;
        private int _count;
        private int _head;

        private readonly float _strokePx;
        private readonly float _maxPeakDip;

        public WaveformDrawable(int historyMs, int tickMs, float strokePx, float maxPeakToPeakDip = 40f)
        {
            if (tickMs <= 0) tickMs = 80;
            _maxSamples = Math.Max(8, (int)Math.Ceiling(historyMs / (double)tickMs));
            _samples = new float[_maxSamples];
            _strokePx = Math.Max(1f, strokePx);
            _maxPeakDip = Math.Max(4f, maxPeakToPeakDip * 0.5f);
        }

        public void Reset()
        {
            _count = 0;
            _head = 0;
            Array.Clear(_samples, 0, _samples.Length);
        }

        public void AddSample(float level01)
        {
            if (level01 < 0) level01 = 0;
            if (level01 > 1) level01 = 1;

            _samples[_head] = level01;
            _head = (_head + 1) % _maxSamples;
            _count = Math.Min(_count + 1, _maxSamples);
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();

            canvas.StrokeSize = _strokePx;
            canvas.StrokeLineCap = LineCap.Round;

            var w = dirtyRect.Width;
            var h = dirtyRect.Height;

            if (w <= 1 || h <= 1)
            {
                canvas.RestoreState();
                return;
            }

            var midY = dirtyRect.Top + h * 0.5f;
            var maxPeak = Math.Min(_maxPeakDip, h * 0.5f - _strokePx);
            if (maxPeak < 1f) maxPeak = h * 0.5f;

            if (_count <= 1)
            {
                canvas.StrokeColor = ColorForLevel(0);
                canvas.DrawLine(dirtyRect.Left, midY, dirtyRect.Right, midY);
                canvas.RestoreState();
                return;
            }

            int start = (_head - _count);
            if (start < 0) start += _maxSamples;

            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % _maxSamples;
                float v = _samples[idx];

                float x = dirtyRect.Left + (w * i / (float)(_count - 1));
                float amp = v * maxPeak;

                canvas.StrokeColor = ColorForLevel(v);
                canvas.DrawLine(x, midY - amp, x, midY + amp);
            }

            canvas.RestoreState();
        }

        private static Color ColorForLevel(float level01)
        {
            var v = Math.Clamp((double)level01, 0.0, 1.0);
            var hue = 240.0 * (1.0 - v);
            var (r, g, b) = HsvToRgb(hue, 1.0, 1.0);
            return ColorFrom16Bit(r, g, b);
        }

        private static (double r, double g, double b) HsvToRgb(double hueDeg, double saturation, double value)
        {
            var c = value * saturation;
            var h = (hueDeg % 360.0) / 60.0;
            var x = c * (1.0 - Math.Abs(h % 2.0 - 1.0));

            double r1 = 0, g1 = 0, b1 = 0;

            if (h >= 0 && h < 1) { r1 = c; g1 = x; b1 = 0; }
            else if (h >= 1 && h < 2) { r1 = x; g1 = c; b1 = 0; }
            else if (h >= 2 && h < 3) { r1 = 0; g1 = c; b1 = x; }
            else if (h >= 3 && h < 4) { r1 = 0; g1 = x; b1 = c; }
            else if (h >= 4 && h < 5) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            var m = value - c;
            return (r1 + m, g1 + m, b1 + m);
        }

        private static Color ColorFrom16Bit(double r01, double g01, double b01)
        {
            ushort r16 = (ushort)Math.Clamp((int)Math.Round(r01 * 65535.0), 0, 65535);
            ushort g16 = (ushort)Math.Clamp((int)Math.Round(g01 * 65535.0), 0, 65535);
            ushort b16 = (ushort)Math.Clamp((int)Math.Round(b01 * 65535.0), 0, 65535);

            return new Color(r16 / 65535f, g16 / 65535f, b16 / 65535f, 1f);
        }
    }
}
