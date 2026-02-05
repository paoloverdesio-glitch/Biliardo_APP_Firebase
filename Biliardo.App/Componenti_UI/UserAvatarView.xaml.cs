// FILE: Biliardo.App\Componenti_UI\UserAvatarView.xaml.cs
// Ottimizzazione: evita creare ImageSource nuove ad ogni rebind (riciclo celle CollectionView).
// Usa UriImageSource con caching e cache condivisa per url/path.

using System;
using System.Collections.Concurrent;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI
{
    public partial class UserAvatarView : ContentView
    {
        // Chiavi: "url:<...>" oppure "path:<...>"
        private static readonly ConcurrentDictionary<string, ImageSource> _cache = new();

        private string _lastKey = "";

        public UserAvatarView()
        {
            InitializeComponent();
            UpdateAvatarSource();
        }

        public static readonly BindableProperty AvatarPathProperty = BindableProperty.Create(
            nameof(AvatarPath),
            typeof(string),
            typeof(UserAvatarView),
            default(string),
            propertyChanged: (_, __, ___) => ((UserAvatarView)_).UpdateAvatarSource());

        public string? AvatarPath
        {
            get => (string?)GetValue(AvatarPathProperty);
            set => SetValue(AvatarPathProperty, value);
        }

        public static readonly BindableProperty AvatarUrlProperty = BindableProperty.Create(
            nameof(AvatarUrl),
            typeof(string),
            typeof(UserAvatarView),
            default(string),
            propertyChanged: (_, __, ___) => ((UserAvatarView)_).UpdateAvatarSource());

        public string? AvatarUrl
        {
            get => (string?)GetValue(AvatarUrlProperty);
            set => SetValue(AvatarUrlProperty, value);
        }

        public static readonly BindableProperty DisplayNameProperty = BindableProperty.Create(
            nameof(DisplayName),
            typeof(string),
            typeof(UserAvatarView),
            default(string),
            propertyChanged: (_, __, ___) => ((UserAvatarView)_).UpdateInitials());

        public string? DisplayName
        {
            get => (string?)GetValue(DisplayNameProperty);
            set => SetValue(DisplayNameProperty, value);
        }

        public const double DefaultSize = 49d;

        public static readonly BindableProperty SizeProperty = BindableProperty.Create(
            nameof(Size),
            typeof(double),
            typeof(UserAvatarView),
            DefaultSize);

        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        public string Initials { get; private set; } = "?";
        public bool HasInitials => !HasImage;
        public bool HasImage => AvatarImage?.Source != null;

        public static void PrimeCache(string avatarPath, ImageSource source)
        {
            if (string.IsNullOrWhiteSpace(avatarPath) || source == null)
                return;

            _cache["path:" + avatarPath.Trim()] = source;
        }

        private void UpdateInitials()
        {
            Initials = BuildInitials(DisplayName);
            OnPropertyChanged(nameof(Initials));
            OnPropertyChanged(nameof(HasInitials));
        }

        private void UpdateAvatarSource()
        {
            ImageSource? source = null;

            var url = (AvatarUrl ?? "").Trim();
            var path = (AvatarPath ?? "").Trim();

            string key = "";
            if (!string.IsNullOrWhiteSpace(url))
                key = "url:" + url;
            else if (!string.IsNullOrWhiteSpace(path))
                key = "path:" + path;

            // Se è la stessa chiave già applicata, evita lavoro e riduci invalidazioni durante scroll.
            if (!string.IsNullOrWhiteSpace(key) && string.Equals(key, _lastKey, StringComparison.Ordinal))
            {
                UpdateInitials();
                return;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (_cache.TryGetValue("url:" + url, out var cached))
                {
                    source = cached;
                }
                else
                {
                    // UriImageSource con caching esplicito
                    var uriSource = new UriImageSource
                    {
                        Uri = new Uri(url),
                        CachingEnabled = true,
                        CacheValidity = TimeSpan.FromDays(7)
                    };

                    _cache["url:" + url] = uriSource;
                    source = uriSource;
                }

                // Se abbiamo anche un AvatarPath, salviamo alias path->source
                if (!string.IsNullOrWhiteSpace(path))
                    _cache["path:" + path] = source!;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(path) && _cache.TryGetValue("path:" + path, out var cached))
                    source = cached;
            }

            _lastKey = key;

            AvatarImage.Source = source;
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(HasInitials));
            UpdateInitials();
        }

        private static string BuildInitials(string? name)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return "?";

            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
                return parts[0].Substring(0, Math.Min(1, parts[0].Length)).ToUpperInvariant();

            var first = parts[0].Substring(0, 1).ToUpperInvariant();
            var last = parts[^1].Substring(0, 1).ToUpperInvariant();
            return $"{first}{last}";
        }
    }
}
