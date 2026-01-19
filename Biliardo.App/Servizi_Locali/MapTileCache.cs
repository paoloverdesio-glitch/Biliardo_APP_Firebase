using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Biliardo.App.Servizi_Locali
{
    public static class MapTileCache
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private static readonly ConcurrentDictionary<string, ImageSource> _memory = new();

        public static async Task<ImageSource?> GetTileAsync(double latitude, double longitude, int zoom = 16, CancellationToken ct = default)
        {
            var (x, y) = ToTileXY(latitude, longitude, zoom);
            var key = $"{zoom}_{x}_{y}";

            if (_memory.TryGetValue(key, out var cached))
                return cached;

            var filePath = Path.Combine(FileSystem.CacheDirectory, $"osm_{key}.png");
            if (File.Exists(filePath))
            {
                var src = ImageSource.FromFile(filePath);
                _memory[key] = src;
                return src;
            }

            var url = $"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png";
            try
            {
                var bytes = await _http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(filePath, bytes, ct);
                var src = ImageSource.FromFile(filePath);
                _memory[key] = src;
                return src;
            }
            catch
            {
                return null;
            }
        }

        public static (int x, int y) ToTileXY(double latitude, double longitude, int zoom)
        {
            var latRad = latitude * Math.PI / 180.0;
            var n = Math.Pow(2.0, zoom);
            var x = (int)Math.Floor((longitude + 180.0) / 360.0 * n);
            var y = (int)Math.Floor((1.0 - Math.Log(Math.Tan(latRad) + (1 / Math.Cos(latRad))) / Math.PI) / 2.0 * n);
            return (x, y);
        }
    }
}
