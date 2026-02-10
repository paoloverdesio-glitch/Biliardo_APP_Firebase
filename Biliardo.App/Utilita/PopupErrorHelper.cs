using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Biliardo.App.Utilita
{
    public static class PopupErrorHelper
    {
        public static Task ShowAsync(Page page, string title, string message)
        {
            if (page == null)
                return Task.CompletedTask;

            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var safeTitle = string.IsNullOrWhiteSpace(title) ? "Errore" : title;
                var safeMessage = message ?? "";

                var modal = new ContentPage
                {
                    BackgroundColor = Color.FromArgb("#80000000"),
                    Content = BuildContent(page, safeTitle, safeMessage)
                };

                await page.Navigation.PushModalAsync(modal, false);
            });
        }

        private static View BuildContent(Page page, string title, string message)
        {
            var titleLabel = new Label
            {
                Text = title,
                FontSize = 18,
                FontAttributes = FontAttributes.Bold,
                TextColor = Colors.White
            };

            var messageLabel = new Label
            {
                Text = message,
                FontSize = 13,
                TextColor = Colors.White,
                LineBreakMode = LineBreakMode.WordWrap
            };

            var scroll = new ScrollView
            {
                Content = messageLabel,
                HeightRequest = 260
            };

            var copyButton = new Button
            {
                Text = "Copia e chiudi",
                BackgroundColor = Color.FromArgb("#25D366"),
                TextColor = Colors.Black
            };

            copyButton.Clicked += async (_, __) =>
            {
                try
                {
                    await Clipboard.Default.SetTextAsync(message);
                }
                catch
                {
                    // best-effort
                }

                await page.Navigation.PopModalAsync(false);
            };

            var frame = new Frame
            {
                BackgroundColor = Color.FromArgb("#1E1E1E"),
                CornerRadius = 14,
                Padding = new Thickness(16),
                Content = new VerticalStackLayout
                {
                    Spacing = 12,
                    Children =
                    {
                        titleLabel,
                        scroll,
                        copyButton
                    }
                }
            };

            return new Grid
            {
                Padding = new Thickness(18, 40),
                Children = { frame }
            };
        }
    }
}
