using System.Diagnostics;

namespace Biliardo.App.Helpers
{
    /// <summary>
    /// Helper centralizzato per le chiamate a Share.
    /// Fornisce metodi per condividere testo/uri e file in modo sicuro e con diagnostica.
    /// </summary>
    public static class ShareHelper
    {
        /// <summary>
        /// Condivide testo o uri se uno dei due è valorizzato.
        /// Restituisce true se la richiesta di condivisione è stata invocata correttamente.
        /// </summary>
        public static async Task<bool> ShareIfNotEmptyAsync(string? text, string? uri, string? title = null)
        {
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(uri))
                return false;

            // sanitize
            var shareText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            var shareUri = string.IsNullOrWhiteSpace(uri) ? null : uri.Trim();
            var shareTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

            Debug.WriteLine($"ShareHelper: request => TextLen={(shareText?.Length ?? 0)}, Uri={(shareUri ?? "<null>")}, Title={(shareTitle ?? "<null>")}");

            var req = new ShareTextRequest
            {
                Text = shareText,
                Uri = shareUri,
                Title = shareTitle
            };

            try
            {
                await Share.Default.RequestAsync(req).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShareHelper: share failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Condivide un file locale se esiste. Restituisce true se la richiesta di condivisione è stata invocata correttamente.
        /// </summary>
        public static async Task<bool> ShareFileAsync(string? filePath, string? title = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var trimmed = filePath!.Trim();
            var exists = File.Exists(trimmed);
            Debug.WriteLine($"ShareHelper: ShareFileAsync => Exists={exists}, Path={(trimmed ?? "<null>")}, Title={(title ?? "<null>")}");

            if (!exists)
                return false;

            var req = new ShareFileRequest
            {
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                File = new ShareFile(trimmed)
            };

            try
            {
                await Share.Default.RequestAsync(req).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShareHelper: share file failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}