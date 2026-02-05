using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Networking;
using Biliardo.App.Servizi_Diagnostics;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Componenti_UI
{
    public partial class SelettoreUtenteAutocomplete : ContentView
    {
        private readonly ObservableCollection<FirestoreDirectoryService.UserPublicItem> _items = new();

        private string? _nextCursor;
        private string _currentPrefix = string.Empty;

        private CancellationTokenSource? _debounceCts;

        public static readonly BindableProperty MinCharsProperty =
            BindableProperty.Create(nameof(MinChars), typeof(int), typeof(SelettoreUtenteAutocomplete), 1);

        public static readonly BindableProperty PageTakeProperty =
            BindableProperty.Create(nameof(PageTake), typeof(int), typeof(SelettoreUtenteAutocomplete), 50);

        public static readonly BindableProperty MaxVisibleProperty =
            BindableProperty.Create(nameof(MaxVisible), typeof(int), typeof(SelettoreUtenteAutocomplete), 10, propertyChanged: OnMaxVisibleChanged);

        public static readonly BindableProperty ExcludeNicknameProperty =
            BindableProperty.Create(nameof(ExcludeNickname), typeof(string), typeof(SelettoreUtenteAutocomplete), default(string));

        // ESCLUSIONE FORTE: per UID (evita “scrivo a me stesso” anche se nickname cambia)
        public static readonly BindableProperty ExcludeUidProperty =
            BindableProperty.Create(nameof(ExcludeUid), typeof(string), typeof(SelettoreUtenteAutocomplete), default(string));

        public int MinChars
        {
            get => (int)GetValue(MinCharsProperty);
            set => SetValue(MinCharsProperty, value);
        }

        public int PageTake
        {
            get => (int)GetValue(PageTakeProperty);
            set => SetValue(PageTakeProperty, value);
        }

        public int MaxVisible
        {
            get => (int)GetValue(MaxVisibleProperty);
            set => SetValue(MaxVisibleProperty, value);
        }

        public string? ExcludeNickname
        {
            get => (string?)GetValue(ExcludeNicknameProperty);
            set => SetValue(ExcludeNicknameProperty, value);
        }

        public string? ExcludeUid
        {
            get => (string?)GetValue(ExcludeUidProperty);
            set => SetValue(ExcludeUidProperty, value);
        }

        public event EventHandler<UtenteSelezionatoEventArgs>? UtenteSelezionato;

        public SelettoreUtenteAutocomplete()
        {
            InitializeComponent();
            ListSuggerimenti.ItemsSource = _items;
            DropPanel.MaximumHeightRequest = 44 * MaxVisible;
        }

        private void OnTextChanged(object? sender, TextChangedEventArgs e)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            _ = DebouncedSearchAsync(_debounceCts.Token);
        }

        private async Task DebouncedSearchAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(250, token);
                if (token.IsCancellationRequested) return;

                var prefix = TxtInput.Text?.Trim() ?? string.Empty;

                if (prefix.Length < MinChars)
                {
                    DiagLog.Note("Directory.Search.Skip", "MinChars");
                    HideDropdown();
                    return;
                }

                if (!string.Equals(prefix, _currentPrefix, StringComparison.Ordinal))
                {
                    _currentPrefix = prefix;
                    _nextCursor = null;
                    _items.Clear();
                }

                DiagLog.Step("Directory.Search", _currentPrefix);
                await LoadPageAsync(initial: _items.Count == 0, token);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Spinner.IsRunning = Spinner.IsVisible = false;
                DiagLog.Exception("Directory.Search", ex);
                await Application.Current?.MainPage?.DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async Task LoadPageAsync(bool initial, CancellationToken token)
        {
            if (Connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                DiagLog.Note("Directory.Search.Network", "Offline");
                LblEmpty.IsVisible = true;
                DropPanel.IsVisible = true;
                return;
            }

            Spinner.IsRunning = Spinner.IsVisible = true;

            var res = await FirestoreDirectoryService.SearchUsersPublicAsync(
                _currentPrefix,
                take: PageTake,
                after: initial ? null : _nextCursor,
                ct: token);

            Spinner.IsRunning = Spinner.IsVisible = false;

            if (initial)
                _items.Clear();

            IEnumerable<FirestoreDirectoryService.UserPublicItem> items = res.Items ?? new List<FirestoreDirectoryService.UserPublicItem>();

            // filtro uid (forte)
            if (!string.IsNullOrWhiteSpace(ExcludeUid))
            {
                var uid = ExcludeUid!.Trim();
                items = items.Where(it => !string.Equals(it.Uid, uid, StringComparison.Ordinal));
            }

            // filtro nickname (secondario)
            if (!string.IsNullOrWhiteSpace(ExcludeNickname))
            {
                var toExclude = ExcludeNickname!;
                items = items.Where(it => !string.Equals(it.Nickname, toExclude, StringComparison.OrdinalIgnoreCase));
            }

            var materialized = items.ToList();

            if (materialized.Count > 0)
            {
                foreach (var it in materialized)
                    _items.Add(it);

                _nextCursor = res.NextCursor;
                LblEmpty.IsVisible = false;
                DropPanel.IsVisible = true;
            }
            else
            {
                _nextCursor = res.NextCursor;
                LblEmpty.IsVisible = true;
                DropPanel.IsVisible = true;
            }
        }

        private async void OnRemainingItemsThresholdReached(object? sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_nextCursor) && DropPanel.IsVisible)
            {
                using var cts = new CancellationTokenSource();
                try { await LoadPageAsync(initial: false, cts.Token); }
                catch { }
            }
        }

        private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            try
            {
                var selected = e.CurrentSelection?.FirstOrDefault() as FirestoreDirectoryService.UserPublicItem;
                if (selected == null) return;

                ListSuggerimenti.SelectedItem = null;

                // blocco extra: evita selezione del proprio uid anche se filtraggio fallisse
                if (!string.IsNullOrWhiteSpace(ExcludeUid) && string.Equals(selected.Uid, ExcludeUid, StringComparison.Ordinal))
                    return;

                TxtInput.Text = selected.Nickname;
                HideDropdown();

                UtenteSelezionato?.Invoke(this, new UtenteSelezionatoEventArgs(selected.Uid, selected.Nickname));
            }
            catch (Exception ex)
            {
                await Application.Current?.MainPage?.DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private void OnFocused(object? sender, FocusEventArgs e)
        {
            if (_items.Count > 0)
            {
                LblEmpty.IsVisible = false;
                DropPanel.IsVisible = true;
            }
        }

        private void OnUnfocused(object? sender, FocusEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                MainThread.BeginInvokeOnMainThread(HideDropdown);
            });
        }

        private static void OnMaxVisibleChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is SelettoreUtenteAutocomplete c && newValue is int n && n > 0)
                c.DropPanel.MaximumHeightRequest = 44 * n;
        }

        private void HideDropdown()
        {
            DropPanel.IsVisible = false;
            LblEmpty.IsVisible = false;
            Spinner.IsRunning = Spinner.IsVisible = false;
        }

        public void Clear()
        {
            _items.Clear();
            _nextCursor = null;
            _currentPrefix = string.Empty;
            TxtInput.Text = string.Empty;
            HideDropdown();
        }

        public void FocusInput() => TxtInput.Focus();
    }

    public sealed class UtenteSelezionatoEventArgs : EventArgs
    {
        public string Id { get; }
        public string Nickname { get; }

        public UtenteSelezionatoEventArgs(string id, string nickname)
        {
            Id = id;
            Nickname = nickname;
        }
    }

    public sealed class ToInitialConverter : IValueConverter
    {
        public static readonly ToInitialConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            return string.IsNullOrWhiteSpace(s) ? "?" : s.Substring(0, 1).ToUpperInvariant();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
