using System;
using Biliardo.App.Effects;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI.Composer
{
    public sealed class MicHoldButton : ContentView
    {
        private readonly TouchEffect _touchEffect;
        private readonly Image _icon;

        public MicHoldButton()
        {
            WidthRequest = 52;
            HeightRequest = 52;

            var frame = new Frame
            {
                CornerRadius = 26,
                Padding = 0,
                BackgroundColor = AccentColor,
                HasShadow = false,
                WidthRequest = 52,
                HeightRequest = 52,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };

            _icon = new Image
            {
                Source = "ic_mic.png",
                WidthRequest = 24,
                HeightRequest = 24,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };

            frame.Content = _icon;
            Content = frame;

            _touchEffect = new TouchEffect { Capture = true };
            _touchEffect.TouchAction += OnTouchAction;
            Effects.Add(_touchEffect);
        }

        public static readonly BindableProperty IconSourceProperty = BindableProperty.Create(
            nameof(IconSource),
            typeof(ImageSource),
            typeof(MicHoldButton),
            default(ImageSource),
            propertyChanged: (bindable, _, newValue) =>
            {
                if (bindable is MicHoldButton btn && newValue is ImageSource src)
                    btn._icon.Source = src;
            });

        public ImageSource IconSource
        {
            get => (ImageSource)GetValue(IconSourceProperty);
            set => SetValue(IconSourceProperty, value);
        }

        public static readonly BindableProperty AccentColorProperty = BindableProperty.Create(
            nameof(AccentColor),
            typeof(Color),
            typeof(MicHoldButton),
            Color.FromArgb("#18A558"),
            propertyChanged: (bindable, _, newValue) =>
            {
                if (bindable is MicHoldButton btn && newValue is Color c && btn.Content is Frame f)
                    f.BackgroundColor = c;
            });

        public Color AccentColor
        {
            get => (Color)GetValue(AccentColorProperty);
            set => SetValue(AccentColorProperty, value);
        }

        public event EventHandler<HoldEventArgs>? HoldStarted;
        public event EventHandler<HoldEventArgs>? HoldMoved;
        public event EventHandler<HoldEventArgs>? HoldEnded;
        public event EventHandler<HoldEventArgs>? HoldCanceled;

        private void OnTouchAction(object sender, TouchActionEventArgs e)
        {
            var args = new HoldEventArgs(e.Location, DateTimeOffset.UtcNow);

            switch (e.Type)
            {
                case TouchActionType.Pressed:
                    HoldStarted?.Invoke(this, args);
                    break;
                case TouchActionType.Moved:
                    HoldMoved?.Invoke(this, args);
                    break;
                case TouchActionType.Released:
                    HoldEnded?.Invoke(this, args);
                    break;
                case TouchActionType.Cancelled:
                    HoldCanceled?.Invoke(this, args);
                    break;
            }
        }
    }

    public sealed class HoldEventArgs : EventArgs
    {
        public HoldEventArgs(Point point, DateTimeOffset timestamp)
        {
            Point = point;
            Timestamp = timestamp;
        }

        public Point Point { get; }
        public DateTimeOffset Timestamp { get; }
    }
}
