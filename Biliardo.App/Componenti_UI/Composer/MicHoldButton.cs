using System;
using System.Diagnostics;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

#if ANDROID
using Android.Views;
using AView = Android.Views.View;
#endif

#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
#endif

namespace Biliardo.App.Componenti_UI.Composer
{
    /// <summary>
    /// Pulsante microfono con eventi "hold" (Pressed/Move/Released/Cancel).
    /// Implementazione DIRETTA su eventi nativi (Android/Windows) per evitare problemi di attach degli Effects in MAUI.
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

        private Point _startPoint;
        private bool _isPressed;

#if ANDROID
        private AView? _androidView;
        private float _androidDensity = 1f;
#endif

#if WINDOWS
        private FrameworkElement? _winView;
#endif

        public MicHoldButton()
        {
            // Dimensioni default per mantenere il cerchio (evita “stiramento” verticale)
            WidthRequest = 44;
            HeightRequest = 44;
            HorizontalOptions = LayoutOptions.Center;
            VerticalOptions = LayoutOptions.Center;

            _icon = new Image
            {
                Source = "ic_mic.png",
                WidthRequest = 22,
                HeightRequest = 22,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true, // NON deve intercettare il touch
            };

            _frame = new Frame
            {
                Padding = 0,
                Margin = 0,
                HasShadow = false,
                CornerRadius = 9999,
                BackgroundColor = AccentColor,

                WidthRequest = 44,
                HeightRequest = 44,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,

                Content = _icon
            };

            Content = _frame;

            // Aggancio nativo quando il controllo ottiene l’Handler (cioè quando diventa “reale” a runtime)
            _frame.HandlerChanging += (_, __) => DetachNative();
            _frame.HandlerChanged += (_, __) => AttachNative();
        }

        private static void OnAccentColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is not MicHoldButton b)
                return;

            if (newValue is Color c)
                b._frame.BackgroundColor = c;
        }

        private void AttachNative()
        {
            DetachNative();

#if ANDROID
            try
            {
                if (_frame.Handler?.PlatformView is AView v)
                {
                    _androidView = v;
                    _androidDensity = v.Context?.Resources?.DisplayMetrics?.Density ?? 1f;

                    try
                    {
                        v.Clickable = true;
                        v.LongClickable = true;
                        v.Focusable = true;
                        v.FocusableInTouchMode = true;
                    }
                    catch { }

                    v.Touch += OnAndroidTouch;

#if DEBUG
                    Android.Util.Log.Info("Biliardo.Mic", "101 - MicHoldButton ANDROID attached to native view");
#endif
                }
            }
            catch { }
#endif

#if WINDOWS
            try
            {
                if (_frame.Handler?.PlatformView is FrameworkElement fe)
                {
                    _winView = fe;
                    fe.PointerPressed += OnWinPointerPressed;
                    fe.PointerMoved += OnWinPointerMoved;
                    fe.PointerReleased += OnWinPointerReleased;
                    fe.PointerCanceled += OnWinPointerCanceled;

#if DEBUG
                    Debug.WriteLine("101 - MicHoldButton WINDOWS attached to native view");
#endif
                }
            }
            catch { }
#endif
        }

        private void DetachNative()
        {
#if ANDROID
            try
            {
                if (_androidView != null)
                    _androidView.Touch -= OnAndroidTouch;
            }
            catch { }
            _androidView = null;
#endif

#if WINDOWS
            try
            {
                if (_winView != null)
                {
                    _winView.PointerPressed -= OnWinPointerPressed;
                    _winView.PointerMoved -= OnWinPointerMoved;
                    _winView.PointerReleased -= OnWinPointerReleased;
                    _winView.PointerCanceled -= OnWinPointerCanceled;
                }
            }
            catch { }
            _winView = null;
#endif

            _isPressed = false;
            _startPoint = default;
        }

#if ANDROID
        private void OnAndroidTouch(object? sender, AView.TouchEventArgs e)
        {
            try
            {
                var me = e.Event;
                var view = _androidView;
                if (me == null || view == null)
                    return;

                var action = me.ActionMasked;

                // Per MOVE, ActionIndex non è affidabile: con singolo dito usiamo 0
                int idx = action == MotionEventActions.Move ? 0 : me.ActionIndex;

                double x = me.GetX(idx) / _androidDensity;
                double y = me.GetY(idx) / _androidDensity;

                switch (action)
                {
                    case MotionEventActions.Down:
                    case MotionEventActions.PointerDown:
                        _isPressed = true;
                        _startPoint = new Point(x, y);

#if DEBUG
                        Android.Util.Log.Info("Biliardo.Mic", $"102 - Pressed @ ({x:0.0},{y:0.0})");
#endif
                        HoldStarted?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                        // blocca intercettazioni parent (scroll ecc.)
                        view.Parent?.RequestDisallowInterceptTouchEvent(true);
                        e.Handled = true;
                        return;

                    case MotionEventActions.Move:
                        if (!_isPressed)
                        {
                            e.Handled = true;
                            return;
                        }

#if DEBUG
                        Android.Util.Log.Info("Biliardo.Mic", $"103 - Moved @ ({x:0.0},{y:0.0})");
#endif
                        HoldMoved?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                        e.Handled = true;
                        return;

                    case MotionEventActions.Up:
                    case MotionEventActions.PointerUp:
                        if (!_isPressed)
                        {
                            e.Handled = true;
                            return;
                        }

                        _isPressed = false;

#if DEBUG
                        Android.Util.Log.Info("Biliardo.Mic", $"104 - Released @ ({x:0.0},{y:0.0})");
#endif
                        HoldEnded?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                        view.Parent?.RequestDisallowInterceptTouchEvent(false);
                        e.Handled = true;
                        return;

                    case MotionEventActions.Cancel:
                        if (!_isPressed)
                        {
                            e.Handled = true;
                            return;
                        }

                        _isPressed = false;

#if DEBUG
                        Android.Util.Log.Info("Biliardo.Mic", $"105 - Cancelled @ ({x:0.0},{y:0.0})");
#endif
                        HoldCanceled?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                        view.Parent?.RequestDisallowInterceptTouchEvent(false);
                        e.Handled = true;
                        return;

                    default:
                        e.Handled = true;
                        return;
                }
            }
            catch
            {
                try { e.Handled = true; } catch { }
            }
        }
#endif

#if WINDOWS
        private void OnWinPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_winView == null) return;
                var pt = e.GetCurrentPoint(_winView);
                var x = pt.Position.X;
                var y = pt.Position.Y;

                _isPressed = true;
                _startPoint = new Point(x, y);

#if DEBUG
                Debug.WriteLine($"102 - Pressed @ ({x:0.0},{y:0.0})");
#endif
                HoldStarted?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                e.Handled = true;
            }
            catch { }
        }

        private void OnWinPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isPressed || _winView == null) { e.Handled = true; return; }
                var pt = e.GetCurrentPoint(_winView);
                var x = pt.Position.X;
                var y = pt.Position.Y;

#if DEBUG
                Debug.WriteLine($"103 - Moved @ ({x:0.0},{y:0.0})");
#endif
                HoldMoved?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                e.Handled = true;
            }
            catch { }
        }

        private void OnWinPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isPressed || _winView == null) { e.Handled = true; return; }
                var pt = e.GetCurrentPoint(_winView);
                var x = pt.Position.X;
                var y = pt.Position.Y;

                _isPressed = false;

#if DEBUG
                Debug.WriteLine($"104 - Released @ ({x:0.0},{y:0.0})");
#endif
                HoldEnded?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                e.Handled = true;
            }
            catch { }
        }

        private void OnWinPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isPressed || _winView == null) { e.Handled = true; return; }
                var pt = e.GetCurrentPoint(_winView);
                var x = pt.Position.X;
                var y = pt.Position.Y;

                _isPressed = false;

#if DEBUG
                Debug.WriteLine($"105 - Cancelled @ ({x:0.0},{y:0.0})");
#endif
                HoldCanceled?.Invoke(this, new HoldEventArgs(new Point(x, y), _startPoint));
                e.Handled = true;
            }
            catch { }
        }
#endif
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
