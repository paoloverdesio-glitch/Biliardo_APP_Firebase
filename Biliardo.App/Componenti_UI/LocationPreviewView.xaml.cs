using System;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Locali;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;

namespace Biliardo.App.Componenti_UI
{
    public partial class LocationPreviewView : ContentView
    {
        public LocationPreviewView()
        {
            InitializeComponent();

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, __) => await OpenMapsAsync();
            GestureRecognizers.Add(tap);
        }

        public static readonly BindableProperty LatitudeProperty = BindableProperty.Create(
            nameof(Latitude),
            typeof(double?),
            typeof(LocationPreviewView),
            default(double?),
            propertyChanged: async (bindable, _, __) =>
            {
                var view = (LocationPreviewView)bindable;
                view.OnPropertyChanged(nameof(AddressLabel));
                await view.RefreshTileAsync();
            });

        public double? Latitude
        {
            get => (double?)GetValue(LatitudeProperty);
            set => SetValue(LatitudeProperty, value);
        }

        public static readonly BindableProperty LongitudeProperty = BindableProperty.Create(
            nameof(Longitude),
            typeof(double?),
            typeof(LocationPreviewView),
            default(double?),
            propertyChanged: async (bindable, _, __) =>
            {
                var view = (LocationPreviewView)bindable;
                view.OnPropertyChanged(nameof(AddressLabel));
                await view.RefreshTileAsync();
            });

        public double? Longitude
        {
            get => (double?)GetValue(LongitudeProperty);
            set => SetValue(LongitudeProperty, value);
        }

        public static readonly BindableProperty AddressProperty = BindableProperty.Create(
            nameof(Address),
            typeof(string),
            typeof(LocationPreviewView),
            default(string),
            propertyChanged: (bindable, _, __) =>
            {
                ((LocationPreviewView)bindable).OnPropertyChanged(nameof(AddressLabel));
            });

        public string? Address
        {
            get => (string?)GetValue(AddressProperty);
            set => SetValue(AddressProperty, value);
        }

        public string AddressLabel
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Address))
                    return Address!;

                if (Latitude.HasValue && Longitude.HasValue)
                    return $"{Latitude:0.0000}, {Longitude:0.0000}";

                return "Posizione";
            }
        }

        private async Task RefreshTileAsync()
        {
            if (!Latitude.HasValue || !Longitude.HasValue)
                return;

            var img = await MapTileCache.GetTileAsync(Latitude.Value, Longitude.Value, 16);
            if (img != null)
                TileImage.Source = img;
        }

        private async Task OpenMapsAsync()
        {
            if (!Latitude.HasValue || !Longitude.HasValue)
                return;

            var location = new Location(Latitude.Value, Longitude.Value);
            var options = new MapLaunchOptions { Name = AddressLabel };

            try
            {
                await Map.Default.OpenAsync(location, options);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
