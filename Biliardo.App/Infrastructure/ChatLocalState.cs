using System;
using Microsoft.Maui.Storage;

namespace Biliardo.App.Infrastructure
{
    public static class ChatLocalState
    {
        private const string KeyPrefix = "chat_cleared_at:";

        public static DateTimeOffset? GetClearedAt(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId))
                return null;

            var ticks = Preferences.Default.Get(KeyPrefix + chatId, 0L);
            if (ticks <= 0)
                return null;

            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        public static void SetClearedAt(string chatId, DateTimeOffset timestampUtc)
        {
            if (string.IsNullOrWhiteSpace(chatId))
                return;

            Preferences.Default.Set(KeyPrefix + chatId, timestampUtc.UtcTicks);
        }

        public static void ClearClearedAt(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId))
                return;

            Preferences.Default.Remove(KeyPrefix + chatId);
        }
    }
}
