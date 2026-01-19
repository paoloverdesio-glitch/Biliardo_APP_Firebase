using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Firebase;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Pagine_Home
{
    public partial class PostDetailPage : ContentPage
    {
        private readonly FirestoreHomeFeedService _homeFeed = new();
        private string? _cursor;

        public PostDetailPage(Pagina_Home.HomePostVm post)
        {
            InitializeComponent();
            Post = post;
            BindingContext = this;
        }

        public Pagina_Home.HomePostVm Post { get; }
        public ObservableCollection<CommentVm> Comments { get; } = new();

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadCommentsAsync();
        }

        private async Task LoadCommentsAsync()
        {
            try
            {
                var res = await _homeFeed.ListCommentsAsync(Post.PostId, 30, _cursor);
                Comments.Clear();
                foreach (var item in res.Items)
                    Comments.Add(CommentVm.FromService(item));
                _cursor = res.NextCursor;
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnSendCommentClicked(object sender, EventArgs e)
        {
            var text = (CommentEntry.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                await _homeFeed.AddCommentAsync(Post.PostId, text);
                CommentEntry.Text = "";
                await LoadCommentsAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnBackClicked(object sender, EventArgs e)
        {
            await Navigation.PopAsync();
        }

        public sealed class CommentVm
        {
            public string CommentId { get; set; } = "";
            public string AuthorUid { get; set; } = "";
            public string AuthorNickname { get; set; } = "";
            public string? AuthorAvatarPath { get; set; }
            public string? AuthorAvatarUrl { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
            public string Text { get; set; } = "";

            public string CreatedAtLabel => CreatedAtUtc.ToLocalTime().ToString("g");

            public static CommentVm FromService(FirestoreHomeFeedService.HomeCommentItem item)
            {
                return new CommentVm
                {
                    CommentId = item.CommentId,
                    AuthorUid = item.AuthorUid,
                    AuthorNickname = item.AuthorNickname,
                    AuthorAvatarPath = item.AuthorAvatarPath,
                    AuthorAvatarUrl = item.AuthorAvatarUrl,
                    CreatedAtUtc = item.CreatedAtUtc,
                    Text = item.Text ?? ""
                };
            }
        }
    }
}
