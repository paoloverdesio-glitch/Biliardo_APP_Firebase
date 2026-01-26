using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI.Media
{
    public partial class PdfAttachmentView : ContentView
    {
        public static readonly BindableProperty FileNameProperty =
            BindableProperty.Create(nameof(FileName), typeof(string), typeof(PdfAttachmentView), string.Empty);

        public static readonly BindableProperty PreviewSourceProperty =
            BindableProperty.Create(nameof(PreviewSource), typeof(ImageSource), typeof(PdfAttachmentView), default(ImageSource));

        public static readonly BindableProperty OpenCommandProperty =
            BindableProperty.Create(nameof(OpenCommand), typeof(ICommand), typeof(PdfAttachmentView));

        public static readonly BindableProperty OpenCommandParameterProperty =
            BindableProperty.Create(nameof(OpenCommandParameter), typeof(object), typeof(PdfAttachmentView));

        public PdfAttachmentView()
        {
            InitializeComponent();

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, __) =>
            {
                if (OpenCommand?.CanExecute(OpenCommandParameter) ?? false)
                    OpenCommand.Execute(OpenCommandParameter);
            };
            GestureRecognizers.Add(tap);
        }

        public string FileName
        {
            get => (string)GetValue(FileNameProperty);
            set => SetValue(FileNameProperty, value);
        }

        public ImageSource? PreviewSource
        {
            get => (ImageSource?)GetValue(PreviewSourceProperty);
            set => SetValue(PreviewSourceProperty, value);
        }

        public ICommand? OpenCommand
        {
            get => (ICommand?)GetValue(OpenCommandProperty);
            set => SetValue(OpenCommandProperty, value);
        }

        public object? OpenCommandParameter
        {
            get => GetValue(OpenCommandParameterProperty);
            set => SetValue(OpenCommandParameterProperty, value);
        }
    }
}
