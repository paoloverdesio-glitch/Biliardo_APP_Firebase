using Microsoft.Maui.Controls;

namespace Biliardo.App.Pagine_Messaggi
{
    public sealed class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate DateSeparatorTemplate { get; set; } = null!;
        public DataTemplate TextTemplate { get; set; } = null!;
        public DataTemplate PhotoTemplate { get; set; } = null!;
        public DataTemplate FileOrVideoTemplate { get; set; } = null!;
        public DataTemplate AudioTemplate { get; set; } = null!;
        public DataTemplate LocationTemplate { get; set; } = null!;
        public DataTemplate ContactTemplate { get; set; } = null!;

        protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
        {
            if (item is not Pagina_MessaggiDettaglio.ChatMessageVm message)
                return TextTemplate;

            if (message.IsDateSeparator)
                return DateSeparatorTemplate;
            if (message.IsPhoto)
                return PhotoTemplate;
            if (message.IsFileOrVideo)
                return FileOrVideoTemplate;
            if (message.IsAudio)
                return AudioTemplate;
            if (message.IsLocation)
                return LocationTemplate;
            if (message.IsContact)
                return ContactTemplate;

            return TextTemplate;
        }
    }
}
