using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform;

#if ANDROID
using Android.Views;
using AView = Android.Views.View;
[assembly: Microsoft.Maui.Controls.ExportEffect(typeof(Biliardo.App.Effects.PlatformTouchEffectAndroid), "Biliardo.App.TouchEffect")]
#endif

#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
[assembly: Microsoft.Maui.Controls.ExportEffect(typeof(Biliardo.App.Effects.PlatformTouchEffectWindows), "Biliardo.App.TouchEffect")]
#endif

namespace Biliardo.App.Effects
{
    public enum TouchActionType
    {
        Pressed,
        Moved,
        Released,
        Cancelled
    }

    public sealed class TouchActionEventArgs : EventArgs
    {
        public TouchActionEventArgs(long id, TouchActionType type, Point location, bool isInContact)
        {
            Id = id;
            Type = type;
            Location = location;
            IsInContact = isInContact;
        }

        public long Id { get; }
        public TouchActionType Type { get; }
        public Point Location { get; }
        public bool IsInContact { get; }
    }

    public delegate void TouchActionEventHandler(object sender, TouchActionEventArgs args);

    public sealed class TouchEffect : RoutingEffect
    {
        public TouchEffect() : base("Biliardo.App.TouchEffect") { }

        /// <summary>
        /// Se true, il touch viene consumato (Handled=true) per impedire che altre gesture “rubino” l’input.
        /// </summary>
        public bool Capture { get; set; } = true;

        public event TouchActionEventHandler? TouchAction;

        internal void Raise(Element element, TouchActionEventArgs args)
        {
            Debug.WriteLine($"[TouchEffect] Raise: {args.Type} at {args.Location} on {element?.GetType().Name}");
            TouchAction?.Invoke(element, args);
        }
    }
}

#if ANDROID
namespace Biliardo.App.Effects
{
    public sealed class PlatformTouchEffectAndroid : PlatformEffect
    {
        AView? _view;
        TouchEffect? _effect;

        protected override void OnAttached()
        {
            Debug.WriteLine("[PlatformTouchEffectAndroid] OnAttached");
            _effect = Element?.Effects?.OfType<TouchEffect>().FirstOrDefault();
            _view = Control as AView ?? Container as AView;
            if (_view == null || _effect == null)
            {
                Debug.WriteLine("[PlatformTouchEffectAndroid] Missing view or effect");
                return;
            }

            _view.Touch += OnTouch;
        }

        protected override void OnDetached()
        {
            Debug.WriteLine("[PlatformTouchEffectAndroid] OnDetached");
            if (_view != null)
                _view.Touch -= OnTouch;

            _view = null;
            _effect = null;
        }

        void OnTouch(object? sender, AView.TouchEventArgs e)
        {
            var effect = _effect;
            var view = _view;
            var me = e.Event;

            if (effect == null || view == null || me == null || Element == null)
                return;

            var density = view.Context?.Resources?.DisplayMetrics?.Density ?? 1f;

            var action = me.ActionMasked;
            var actionIndex = me.ActionIndex;

            long id = me.GetPointerId(actionIndex);

            double x = me.GetX(actionIndex) / density;
            double y = me.GetY(actionIndex) / density;

            TouchActionType type;
            bool inContact;

            switch (action)
            {
                case MotionEventActions.Down:
                case MotionEventActions.PointerDown:
                    type = TouchActionType.Pressed;
                    inContact = true;
                    if (effect.Capture)
                        view.Parent?.RequestDisallowInterceptTouchEvent(true);
                    break;

                case MotionEventActions.Move:
                    type = TouchActionType.Moved;
                    inContact = true;
                    break;

                case MotionEventActions.Up:
                case MotionEventActions.PointerUp:
                    type = TouchActionType.Released;
                    inContact = false;
                    if (effect.Capture)
                        view.Parent?.RequestDisallowInterceptTouchEvent(false);
                    break;

                case MotionEventActions.Cancel:
                    type = TouchActionType.Cancelled;
                    inContact = false;
                    if (effect.Capture)
                        view.Parent?.RequestDisallowInterceptTouchEvent(false);
                    break;

                default:
                    return;
            }

            Debug.WriteLine($"[PlatformTouchEffectAndroid] OnTouch: {type} @ ({x:0.0},{y:0.0}) handled={effect.Capture}");
            effect.Raise(Element, new TouchActionEventArgs(id, type, new Point(x, y), inContact));
            e.Handled = effect.Capture;
        }
    }
}
#endif

#if WINDOWS
namespace Biliardo.App.Effects
{
    public sealed class PlatformTouchEffectWindows : PlatformEffect
    {
        FrameworkElement? _view;
        TouchEffect? _effect;

        protected override void OnAttached()
        {
            Debug.WriteLine("[PlatformTouchEffectWindows] OnAttached");
            _effect = Element?.Effects?.OfType<TouchEffect>().FirstOrDefault();
            _view = Control as FrameworkElement ?? Container as FrameworkElement;
            if (_view == null || _effect == null)
                return;

            _view.PointerPressed += OnPointerPressed;
            _view.PointerMoved += OnPointerMoved;
            _view.PointerReleased += OnPointerReleased;
            _view.PointerCanceled += OnPointerCanceled;
        }

        protected override void OnDetached()
        {
            if (_view != null)
            {
                _view.PointerPressed -= OnPointerPressed;
                _view.PointerMoved -= OnPointerMoved;
                _view.PointerReleased -= OnPointerReleased;
                _view.PointerCanceled -= OnPointerCanceled;
            }

            _view = null;
            _effect = null;
        }

        void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Raise(e, TouchActionType.Pressed, true);
            e.Handled = _effect?.Capture ?? false;
        }

        void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            Raise(e, TouchActionType.Moved, true);
            e.Handled = _effect?.Capture ?? false;
        }

        void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            Raise(e, TouchActionType.Released, false);
            e.Handled = _effect?.Capture ?? false;
        }

        void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            Raise(e, TouchActionType.Cancelled, false);
            e.Handled = _effect?.Capture ?? false;
        }

        void Raise(PointerRoutedEventArgs e, TouchActionType type, bool inContact)
        {
            var effect = _effect;
            var view = _view;

            if (effect == null || view == null || Element == null)
                return;

            var pt = e.GetCurrentPoint(view);

            long id = (long)pt.PointerId;
            double x = pt.Position.X; // già in DIPs
            double y = pt.Position.Y;

            Debug.WriteLine($"[PlatformTouchEffectWindows] Raise: {type} @ ({x:0.0},{y:0.0})");
            effect.Raise(Element, new TouchActionEventArgs(id, type, new Point(x, y), inContact));
        }
    }
}
#endif
