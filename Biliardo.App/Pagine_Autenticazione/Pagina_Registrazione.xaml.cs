using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Pagine_Autenticazione;

public partial class Pagina_Registrazione : ContentPage
{
    private bool _busy;
    private bool _pwdVisible;
    private string? _avatarLocalPath;

    private TaskCompletionSource<bool>? _popupTcs;

    private const string K_AuthProvider = "auth_provider"; // "firebase"
    private const string ProjectId = "biliardoapp";         // Firestore projectId (lo usano i tuoi servizi REST)

    public Pagina_Registrazione()
    {
        InitializeComponent();
    }

    private async void OnSelectAvatarClicked(object sender, EventArgs e)
    {
        try
        {
            var options = new List<string> { "Galleria" };
            if (MediaPicker.Default.IsCaptureSupported)
                options.Add("Fotocamera");

            options.Add("Annulla");

            var choice = await DisplayActionSheet("Seleziona foto profilo", "Annulla", null, options.ToArray());
            if (string.IsNullOrWhiteSpace(choice) || choice == "Annulla")
                return;

            FileResult? file = null;
            if (choice == "Fotocamera")
                file = await MediaPicker.Default.CapturePhotoAsync();
            else
                file = await MediaPicker.Default.PickPhotoAsync();

            if (file == null)
                return;

            var local = Path.Combine(FileSystem.CacheDirectory, $"avatar_{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            await using var stream = await file.OpenReadAsync();
            await using var fs = File.Create(local);
            await stream.CopyToAsync(fs);

            _avatarLocalPath = local;
            AvatarPreview.Source = ImageSource.FromFile(local);
            AvatarHintLabel.Text = "Foto selezionata";
        }
        catch (Exception ex)
        {
            await ShowPopupAsync(ex.Message, "Errore avatar");
        }
    }

    private void TogglePassword_Clicked(object sender, EventArgs e)
    {
        _pwdVisible = !_pwdVisible;
        PasswordEntry.IsPassword = !_pwdVisible;
        if (sender is Button b)
            b.Text = _pwdVisible ? "🙈" : "👁";
    }

    private Task ShowPopupAsync(string message, string title)
    {
        PopupTitleLabel.Text = title;
        PopupMessageLabel.Text = message;

        PopupOverlay.IsVisible = true;
        _popupTcs = new TaskCompletionSource<bool>();
        return _popupTcs.Task;
    }

    private void OnPopupOkClicked(object sender, EventArgs e)
    {
        PopupOverlay.IsVisible = false;
        _popupTcs?.TrySetResult(true);
        _popupTcs = null;
    }

    private void OnTornaLogin(object sender, EventArgs e)
    {
        Application.Current.MainPage = new NavigationPage(new Pagina_Login(showInserisciCredenziali: true));
    }

    private async void OnRegistratiFirebase(object sender, EventArgs e)
    {
        if (_busy) return;

        var nickname = (NicknameEntry.Text ?? "").Trim();
        var email = (EmailEntry.Text ?? "").Trim();
        var pwd = PasswordEntry.Text ?? "";

        if (string.IsNullOrWhiteSpace(nickname) ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(pwd))
        {
            await ShowPopupAsync("Compila Nickname, Email e Password.", "Errore");
            return;
        }

        // Nickname: 3–20, solo A-Z a-z 0-9 _ . -
        if (!Regex.IsMatch(nickname, "^[A-Za-z0-9_.-]{3,20}$"))
        {
            await ShowPopupAsync("Nickname non valido.\nUsa 3–20 caratteri: A-Z a-z 0-9 _ . -", "Errore");
            return;
        }

        if (!email.Contains("@", StringComparison.Ordinal) || !email.Contains(".", StringComparison.Ordinal))
        {
            await ShowPopupAsync("Email non valida.", "Errore");
            return;
        }

        if (Connectivity.NetworkAccess != NetworkAccess.Internet)
        {
            await ShowPopupAsync("Nessuna connessione Internet.", "Errore");
            return;
        }

        // Opzionali
        var nome = NomeEntry.Text?.Trim();
        var cognome = CognomeEntry.Text?.Trim();
        var citta = CittaEntry.Text?.Trim();
        var circolo = CircoloEntry.Text?.Trim();
        var tel = TelefonoEntry.Text?.Trim();
        int? eta = null;
        if (int.TryParse(EtaEntry.Text, out var etaVal)) eta = etaVal;
        var categoria = CategoriaPicker.SelectedItem as string;

        _busy = true;
        if (sender is Button b1) b1.IsEnabled = false;

        try
        {
            try { await SecureStorage.Default.SetAsync(K_AuthProvider, "firebase"); } catch { }

            // 1) Crea account Firebase Auth
            var signUp = await FirebaseAuthClient.SignUpAsync(email, pwd, CancellationToken.None);

            var uid = signUp.LocalId;
            var idToken = signUp.IdToken;

            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Firebase SignUp: risposta incompleta (uid/idToken).");

            // 2) Invia email verifica (link) - best effort
            try { await FirebaseAuthClient.SendEmailVerificationAsync(idToken, CancellationToken.None); }
            catch { /* non bloccare */ }

            // 3) Firestore: prova a scrivere profilo + nickname (se le regole lo permettono)
            //    Se le rules bloccano (PERMISSION_DENIED), l'account Auth esiste comunque.
            var now = DateTimeOffset.UtcNow;
            var nicknameLower = nickname.ToLowerInvariant();

            // 3.1) Nickname uniqueness: nicknames/{nicknameLower}
            try
            {
                var nickFields = new Dictionary<string, object>
                {
                    ["uid"] = FirestoreRestClient.VString(uid),
                    ["createdAt"] = FirestoreRestClient.VTimestamp(now)
                };

                await FirestoreRestClient.CreateDocumentAsync(
                    collectionPath: "nicknames",
                    documentId: nicknameLower,
                    fields: nickFields,
                    idToken: idToken,
                    ct: CancellationToken.None);
            }
            catch (Exception exNick)
            {
                var m = exNick.Message ?? "";
                if (m.Contains("409", StringComparison.OrdinalIgnoreCase) ||
                    m.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    await ShowPopupAsync(
                        "NIKNAME REGISTRATO.\n\nScegli un altro nickname e riprova.",
                        "Errore registrazione Firebase");
                    return;
                }
                // Se qui fallisce per rules, lo gestiamo sotto come “profilo non scritto”
                throw;
            }

            // 3.2) users_public/{uid}
            try
            {
                var userFields = new Dictionary<string, object>
                {
                    ["nickname"] = FirestoreRestClient.VString(nickname),
                    ["nicknameLower"] = FirestoreRestClient.VString(nicknameLower),
                    ["createdAt"] = FirestoreRestClient.VTimestamp(now),
                    ["updatedAt"] = FirestoreRestClient.VTimestamp(now)
                };

                AddIfNotEmpty(userFields, "nome", nome);
                AddIfNotEmpty(userFields, "cognome", cognome);
                AddIfNotEmpty(userFields, "citta", citta);
                AddIfNotEmpty(userFields, "circolo", circolo);
                AddIfNotEmpty(userFields, "telefono", tel);
                if (eta.HasValue) userFields["eta"] = FirestoreRestClient.VInt(eta.Value);
                AddIfNotEmpty(userFields, "categoriaFISBB", categoria);

                await FirestoreRestClient.CreateDocumentAsync(
                    collectionPath: "users_public",
                    documentId: uid,
                    fields: userFields,
                    idToken: idToken,
                    ct: CancellationToken.None);
            }
            catch (Exception exFs)
            {
                var m = exFs.Message ?? "";
                if (m.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase) ||
                    m.Contains("403", StringComparison.OrdinalIgnoreCase))
                {
                    await ShowPopupAsync(
                        "Account Firebase creato.\n\nATTENZIONE: Firestore ha rifiutato la scrittura (rules).\nVerifica l'email (link) e poi fai login: sistemiamo le regole o l'onboarding nickname.",
                        "Registrazione Firebase");
                    Application.Current.MainPage = new NavigationPage(new Pagina_Login(showInserisciCredenziali: true));
                    return;
                }
                throw;
            }

            // 3.3) Avatar (se selezionato)
            if (!string.IsNullOrWhiteSpace(_avatarLocalPath))
            {
                try
                {
                    var ext = Path.GetExtension(_avatarLocalPath);
                    if (string.IsNullOrWhiteSpace(ext))
                        ext = ".jpg";

                    var storagePath = $"avatars/{uid}/profile{ext}";

                    // ✅ custom metadata per rispettare le Storage Rules (evita 403 se controllano ownerUid/scope)
                    var meta = new Dictionary<string, string>
                    {
                        ["ownerUid"] = uid,
                        ["scope"] = "avatar"
                    };

                    var upload = await FirebaseStorageRestClient.UploadFileWithResultAsync(
                        idToken: idToken,
                        objectPath: storagePath,
                        localFilePath: _avatarLocalPath!,
                        contentType: FirebaseStorageRestClient.GuessContentTypeFromPath(storagePath),
                        customMetadata: meta,
                        ct: CancellationToken.None);

                    var avatarFields = new Dictionary<string, object>
                    {
                        ["avatarPath"] = FirestoreRestClient.VString(upload.StoragePath),
                        ["avatarUrl"] = string.IsNullOrWhiteSpace(upload.DownloadUrl) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(upload.DownloadUrl),
                        ["avatarUpdatedAt"] = FirestoreRestClient.VTimestamp(now)
                    };

                    await FirestoreRestClient.PatchDocumentAsync(
                        $"users_public/{uid}",
                        avatarFields,
                        new[] { "avatarPath", "avatarUrl", "avatarUpdatedAt" },
                        idToken,
                        CancellationToken.None);
                }
                catch
                {
                    // non bloccare la registrazione
                }
            }

            // 4) OK
            await ShowPopupAsync(
                "Account Firebase creato.\n\nTi ho inviato l'email di verifica (link).\nVerifica e poi fai login con Firebase.",
                "Registrazione Firebase");

            Application.Current.MainPage = new NavigationPage(new Pagina_Login(showInserisciCredenziali: true));
        }
        catch (Exception ex)
        {
            await ShowPopupAsync(HumanizeFirebaseError(ex.Message), "Errore Firebase");
        }
        finally
        {
            _busy = false;
            if (sender is Button b2) b2.IsEnabled = true;
        }
    }

    private static void AddIfNotEmpty(Dictionary<string, object> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields[name] = FirestoreRestClient.VString(value.Trim());
    }

    private static string HumanizeFirebaseError(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return "Errore Firebase.";

        var msg = rawMessage.Trim();
        const string prefix = "FirebaseAuth error:";
        if (msg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            msg = msg.Substring(prefix.Length).Trim();

        return msg switch
        {
            "EMAIL_EXISTS" => "Email già registrata.",
            "INVALID_EMAIL" => "Email non valida.",
            "OPERATION_NOT_ALLOWED" => "Provider email/password non abilitato su Firebase.",
            "WEAK_PASSWORD : Password should be at least 6 characters" => "Password troppo debole (minimo 6).",
            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Troppi tentativi. Riprova più tardi.",
            _ => rawMessage
        };
    }
}
