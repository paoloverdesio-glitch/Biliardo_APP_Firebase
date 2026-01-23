using System;

using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

using Biliardo.App.Effects;

namespace Biliardo.App.Componenti_UI.Composer
{
    /// <summary>
    /// Pulsante microfono con eventi "hold" (Pressed/Move/Released/Cancel) basati su TouchEffect.
    /// Nota: il riconoscimento del long-press e le soglie (lock/cancel) sono gestite dal chiamante (ComposerBarView).
    /// </summary>
    public sealed class MicHoldButton : ContentView
    {
        public static readonly BindableProperty AccentColorProperty =
            BindableProperty.Create(
                nameof(AccentColor),
                typeof(Color),
                typeof(MicHoldButton),
                Colors.LimeGreen,
                propertyChanged: OnAccentColorChanged);

        public Color AccentColor
        {
            get => (Color)GetValue(AccentColorProperty);
            set => SetValue(AccentColorProperty, value);
        }

        /// <summary>Icona del microfono (default: "ic_mic.png").</summary>
        public string IconSource
        {
            get => _icon.Source is FileImageSource f ? (f.File ?? "ic_mic.png") : "ic_mic.png";
            set => _icon.Source = value;
        }

        public event EventHandler<HoldEventArgs>? HoldStarted;
        public event EventHandler<HoldEventArgs>? HoldMoved;
        public event EventHandler<HoldEventArgs>? HoldEnded;
        public event EventHandler<HoldEventArgs>? HoldCanceled;

        private readonly Frame _frame;
        private readonly Image _icon;
        private readonly TouchEffect _touchEffect;

        private Point _startPoint;
        private bool _isPressed;

        public MicHoldButton()
        {
            // Frame circolare (la parte che deve ricevere davvero il touch)
            _icon = new Image
            {
                Source = "ic_mic.png",
                WidthRequest = 22,
                HeightRequest = 22,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                // IMPORTANTISSIMO: l'icona non deve intercettare il touch, deve "passare" al Frame.
                InputTransparent = true,
            };

            _frame = new Frame
            {
                Padding = 0,
                Margin = 0,
                HasShadow = false,
                CornerRadius = 9999, // forzatura cerchio
                BackgroundColor = AccentColor,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                Content = _icon
            };

            Content = _frame;

            // TouchEffect va sul FRAME (non sul ContentView padre), altrimenti spesso non arrivano gli eventi.
            _touchEffect = new TouchEffect { Capture = true };
            _touchEffect.TouchAction += OnTouchEffectAction;
            _frame.Effects.Add(_touchEffect);
        }

        private static void OnAccentColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is not MicHoldButton b)
                return;

            try
            {
                if (newValue is Color c)
                    b._frame.BackgroundColor = c;
            }
            catch
            {
                // no-throw: non deve mai rompere la UI
            }
        }

        private void OnTouchEffectAction(object? sender, TouchActionEventArgs args)
        {
            try
            {
                switch (args.Type)
                {
                    case TouchActionType.Pressed:
                        _isPressed = true;
                        _startPoint = args.Location;
                        HoldStarted?.Invoke(this, new HoldEventArgs(args.Location, _startPoint));
                        break;

                    case TouchActionType.Moved:
                        if (!_isPressed) return;
                        HoldMoved?.Invoke(this, new HoldEventArgs(args.Location, _startPoint));
                        break;

                    case TouchActionType.Released:
                        if (!_isPressed) return;
                        _isPressed = false;
                        HoldEnded?.Invoke(this, new HoldEventArgs(args.Location, _startPoint));
                        break;

                    case TouchActionType.Cancelled:
                        if (!_isPressed) return;
                        _isPressed = false;
                        HoldCanceled?.Invoke(this, new HoldEventArgs(args.Location, _startPoint));
                        break;
                }
            }
            catch
            {
                // no-throw: mai crash da gesture
            }
        }
    }

    public sealed class HoldEventArgs : EventArgs
    {
        public HoldEventArgs(Point current, Point start)
        {
            Point = current;
            StartPoint = start;

            TotalX = current.X - start.X;
            TotalY = current.Y - start.Y;
        }

        public Point Point { get; }
        public Point StartPoint { get; }
        public double TotalX { get; }
        public double TotalY { get; }
    }
}
