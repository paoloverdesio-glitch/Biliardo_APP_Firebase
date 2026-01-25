using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;

// Alias
using Color = Microsoft.Maui.Graphics.Color;
using RectF = Microsoft.Maui.Graphics.RectF;
using Path = System.IO.Path;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio : ContentPage
    {
        // ============================================================
        // 1) INIT VOICE SUBSYSTEM
        //    - Da chiamare nel costruttore (nel tuo File 1 finale)
        // ============================================================
        private partial void InitVoiceSubsystem()
        {
            // 1.1) Drawable waveform (stesso comportamento del file originale)
            _voiceWave = new VoiceWaveDrawable(
                historyMs: VOICE_WAVE_HISTORY_MS,
                tickMs: VOICE_UI_TICK_MS,
                strokePx: VOICE_WAVE_STROKE_PX);

            // 1.2) Aggancio alle GraphicsView (presenti come placeholder in XAML)
            try
            {
                VoiceWaveHoldView.Drawable = _voiceWave;
                VoiceWaveLockView.Drawable = _voiceWave;
            }
            catch
            {
                // Se i placeholder non sono disponibili o non inizializzati, non bloccare.
            }
        }

        // ============================================================
        // 2) HANDLER VOCALE: MIC DOWN / UP / PAN
        // ============================================================
        private async void OnMicPressed(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (_voice.IsRecording)
                    return;

                _micIsPressed = true;
                _voiceLocked = false;
                _voiceCanceledBySwipe = false;

                _voiceDurationMs = 0;

                _voiceFilePath = Path.Combine(
                    FileSystem.CacheDirectory,
                    $"voice_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.m4a");

                // 2.1) Permesso microfono
                var st = await Permissions.RequestAsync<Permissions.Microphone>();
                if (st != PermissionStatus.Granted)
                {
                    _micIsPressed = false;
                    _voiceLocked = false;
                    _voiceCanceledBySwipe = false;
                    RefreshVoiceBindings();
                    return;
                }

                // 2.2) Start recorder
                await _voice.StartAsync(_voiceFilePath);

                _voiceWave?.Reset();
                _recordingWaveform.Clear();
                _lastWaveformSampleTicks = 0;

                StartVoiceUiLoop();
                RefreshVoiceBindings();
            }
            catch
            {
                _micIsPressed = false;
                try { await SafeCancelVoiceAsync(); } catch { }
                RefreshVoiceBindings();
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private async void OnMicReleased(object sender, EventArgs e)
        {
            await HandleMicPointerUpAsync();
        }

        private async void OnMicPanUpdated(object sender, PanUpdatedEventArgs e)
        {
            try
            {
                // 2.3) Fine gesture -> equivale a pointer-up
                if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                {
                    await HandleMicPointerUpAsync();
                    return;
                }

                if (!_micIsPressed || !_voice.IsRecording || _voiceCanceledBySwipe)
                    return;

                if (e.StatusType != GestureStatus.Running)
                    return;

                // 2.4) Lock: swipe su
                if (!_voiceLocked && e.TotalY <= MIC_LOCK_DY)
                {
                    _voiceLocked = true;
                    RefreshVoiceBindings();
                    return;
                }

                // 2.5) Cancel: swipe sinistra (solo se non lock)
                if (!_voiceLocked && e.TotalX <= MIC_CANCEL_DX)
                {
                    _voiceCanceledBySwipe = true;
                    await SafeCancelVoiceAsync();
                    RefreshVoiceBindings();
                    return;
                }
            }
            catch { }
        }

        private async Task HandleMicPointerUpAsync()
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                _micIsPressed = false;

                // 2.6) Se non sto registrando, aggiorna binding e basta
                if (!_voice.IsRecording)
                {
                    RefreshVoiceBindings();
                    return;
                }

                // 2.7) Se era già cancellato via swipe, non fare altro
                if (_voiceCanceledBySwipe)
                {
                    RefreshVoiceBindings();
                    return;
                }

                // 2.8) Se in lock, non stoppo su pointer-up (WhatsApp-like)
                if (_voiceLocked)
                {
                    RefreshVoiceBindings();
                    return;
                }

                // 2.9) Stop + invio (solo modalità hold)
                var (filePath, ms) = await StopVoiceAndGetFileAsync();
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    RefreshVoiceBindings();
                    return;
                }

                // 2.10) Scarta se durata troppo corta
                if (ms < VOICE_MIN_SEND_MS)
                {
                    TryDeleteFile(filePath);
                    RefreshVoiceBindings();
                    return;
                }

                await SendVoiceFileAsync(filePath, ms);
                TryDeleteFile(filePath);

                _userNearBottom = true;
                await LoadOnceAsync(CancellationToken.None);

                RefreshVoiceBindings();
            }
            catch
            {
                try { await SafeCancelVoiceAsync(); } catch { }
                RefreshVoiceBindings();
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        // ============================================================
        // 3) PULSANTI PANEL LOCK: TRASH / PAUSE / SEND
        // ============================================================
        private async void OnVoiceTrashClicked(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                _voiceCanceledBySwipe = true;
                await SafeCancelVoiceAsync();
                RefreshVoiceBindings();
            }
            catch { }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private async void OnVoicePauseResumeClicked(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (!_voice.IsRecording) return;
                if (!_voiceLocked) return;

                if (_voice.IsPaused)
                    await _voice.ResumeAsync();
                else
                    await _voice.PauseAsync();

                RefreshVoiceBindings();
            }
            catch { }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private async void OnVoiceSendClicked(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (!_voice.IsRecording) return;
                if (!_voiceLocked) return;

                var (filePath, ms) = await StopVoiceAndGetFileAsync();
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    RefreshVoiceBindings();
                    return;
                }

                if (ms < VOICE_MIN_SEND_MS)
                {
                    TryDeleteFile(filePath);
                    RefreshVoiceBindings();
                    return;
                }

                await SendVoiceFileAsync(filePath, ms);
                TryDeleteFile(filePath);

                _userNearBottom = true;
                await LoadOnceAsync(CancellationToken.None);

                RefreshVoiceBindings();
            }
            catch
            {
                try { await SafeCancelVoiceAsync(); } catch { }
                RefreshVoiceBindings();
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        // ============================================================
        // 4) UI LOOP: TIME LABEL + WAVEFORM
        // ============================================================
        private void StartVoiceUiLoop()
        {
            StopVoiceUiLoop();

            _voiceUiCts = new CancellationTokenSource();
            var token = _voiceUiCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_voice.IsRecording)
                            break;

                        var level = _voice.TryGetLevel01();
                        var ms = _voice.GetElapsedMs();
                        var nowTicks = DateTime.UtcNow.Ticks;

                        if (nowTicks - _lastWaveformSampleTicks >= TimeSpan.FromMilliseconds(AppMediaOptions.AudioWaveformSampleIntervalMs).Ticks)
                        {
                            _lastWaveformSampleTicks = nowTicks;
                            var sample = (int)Math.Clamp(level * 100, 0, 100);
                            lock (_recordingWaveform)
                            {
                                if (_recordingWaveform.Count >= AppMediaOptions.AudioWaveformMaxSamples)
                                    _recordingWaveform.RemoveAt(0);
                                _recordingWaveform.Add(sample);
                            }
                        }

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            VoiceTimeLabel = FormatMs(ms);

                            _voiceWave?.AddSample((float)level);

                            try
                            {
                                VoiceWaveHoldView?.Invalidate();
                                VoiceWaveLockView?.Invalidate();
                            }
                            catch { }

                            // binding per visibilità/label
                            OnPropertyChanged(nameof(IsVoiceHoldStripVisible));
                            OnPropertyChanged(nameof(IsVoiceLockPanelVisible));
                            OnPropertyChanged(nameof(IsNormalComposerVisible));
                            OnPropertyChanged(nameof(VoicePauseResumeLabel));
                        });

                        await Task.Delay(VOICE_UI_TICK_MS, token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, token);
        }

        private void StopVoiceUiLoop()
        {
            try { _voiceUiCts?.Cancel(); } catch { }
            try { _voiceUiCts?.Dispose(); } catch { }
            _voiceUiCts = null;
        }

        // ============================================================
        // 5) STOP / CANCEL SAFE + INVIO
        // ============================================================
        private async Task SafeCancelVoiceAsync()
        {
            try { await _voice.CancelAsync(); } catch { }

            StopVoiceUiLoop();

            if (!string.IsNullOrWhiteSpace(_voiceFilePath))
                TryDeleteFile(_voiceFilePath);

            _voiceFilePath = null;
            _voiceDurationMs = 0;
            VoiceTimeLabel = "00:00";

            _micIsPressed = false;
            _voiceLocked = false;
            _voiceCanceledBySwipe = false;

            _voiceWave?.Reset();

            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        VoiceWaveHoldView?.Invalidate();
                        VoiceWaveLockView?.Invalidate();
                    }
                    catch { }
                });
            }
            catch { }
        }

        private async Task<(string? filePath, long ms)> StopVoiceAndGetFileAsync()
        {
            if (!_voice.IsRecording)
                return (null, 0);

            try { await _voice.StopAsync(); } catch { }

            StopVoiceUiLoop();

            var filePath = _voice.CurrentFilePath ?? _voiceFilePath;
            var ms = _voice.GetElapsedMs();

            _voiceFilePath = null;
            _voiceDurationMs = 0;
            VoiceTimeLabel = "00:00";

            _micIsPressed = false;
            _voiceLocked = false;
            _voiceCanceledBySwipe = false;

            _voiceWave?.Reset();

            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        VoiceWaveHoldView?.Invalidate();
                        VoiceWaveLockView?.Invalidate();
                    }
                    catch { }
                });
            }
            catch { }

            return (filePath, ms);
        }

        private async Task SendVoiceFileAsync(string filePath, long durationMs)
        {
            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            var myUid = FirebaseSessionePersistente.GetLocalId();

            if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login.");

            var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);

            var a = new AttachmentVm
            {
                Kind = AttachmentKind.Audio,
                DisplayName = Path.GetFileName(filePath),
                LocalPath = filePath,
                ContentType = "audio/mp4",
                SizeBytes = new FileInfo(filePath).Length,
                DurationMs = durationMs > 0 ? durationMs : MediaMetadataHelper.TryGetDurationMs(filePath),
                Waveform = GetRecordingWaveformSnapshot()
            };

            await SendAttachmentAsync(idToken!, myUid!, peerId, chatId, a);
        }

        private void RefreshVoiceBindings()
        {
            OnPropertyChanged(nameof(CanShowMic));
            OnPropertyChanged(nameof(IsVoiceHoldStripVisible));
            OnPropertyChanged(nameof(IsVoiceLockPanelVisible));
            OnPropertyChanged(nameof(VoicePauseResumeLabel));
            OnPropertyChanged(nameof(IsNormalComposerVisible));
        }

        private IReadOnlyList<int>? GetRecordingWaveformSnapshot()
        {
            lock (_recordingWaveform)
            {
                if (_recordingWaveform.Count == 0)
                    return null;

                return _recordingWaveform.ToArray();
            }
        }

        // ============================================================
        // 6) HELPERS: FORMAT / DELETE
        // ============================================================
        private static string FormatMs(long ms)
        {
            if (ms < 0) ms = 0;

            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalHours >= 1)
                return ts.ToString(@"hh\:mm\:ss");

            return ts.ToString(@"mm\:ss");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        // ============================================================
        // 7) DRAWABLE: WAVEFORM (GraphicsView)
        // ============================================================
        private sealed class VoiceWaveDrawable : IDrawable
        {
            private readonly int _maxSamples;
            private readonly float[] _samples;
            private int _count;
            private int _head;

            private readonly float _strokePx;

            // Colori waveform (come file originale)
            private readonly Color _cBlue = Color.FromArgb("#1E90FF");
            private readonly Color _cYellow = Color.FromArgb("#FFD60A");
            private readonly Color _cOrange = Color.FromArgb("#FF9F0A");
            private readonly Color _cRed = Color.FromArgb("#FF3B30");

            public VoiceWaveDrawable(int historyMs, int tickMs, float strokePx)
            {
                if (tickMs <= 0) tickMs = 80;
                _maxSamples = Math.Max(8, (int)Math.Ceiling(historyMs / (double)tickMs));
                _samples = new float[_maxSamples];
                _strokePx = Math.Max(1f, strokePx);
            }

            public void Reset()
            {
                _count = 0;
                _head = 0;
                Array.Clear(_samples, 0, _samples.Length);
            }

            public void AddSample(float level01)
            {
                if (level01 < 0) level01 = 0;
                if (level01 > 1) level01 = 1;

                _samples[_head] = level01;
                _head = (_head + 1) % _maxSamples;
                _count = Math.Min(_count + 1, _maxSamples);
            }

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                canvas.SaveState();

                canvas.StrokeSize = _strokePx;
                canvas.StrokeLineCap = LineCap.Round;

                var w = dirtyRect.Width;
                var h = dirtyRect.Height;

                if (w <= 1 || h <= 1 || _count <= 1)
                {
                    canvas.RestoreState();
                    return;
                }

                var midY = dirtyRect.Top + h * 0.5f;
                var amp = h * 0.45f;

                int start = (_head - _count);
                if (start < 0) start += _maxSamples;

                float prevX = dirtyRect.Left;
                float prevY = midY;

                for (int i = 0; i < _count; i++)
                {
                    int idx = (start + i) % _maxSamples;
                    float v = _samples[idx];

                    float x = dirtyRect.Left + (w * i / (float)(_count - 1));
                    float y = midY - (v * amp);

                    canvas.StrokeColor = ColorForLevel(v);

                    if (i > 0)
                        canvas.DrawLine(prevX, prevY, x, y);

                    prevX = x;
                    prevY = y;
                }

                canvas.RestoreState();
            }

            private Color ColorForLevel(float v)
            {
                if (v <= 0.15f) return Lerp(_cBlue, _cYellow, v / 0.15f);
                if (v <= 0.45f) return Lerp(_cYellow, _cOrange, (v - 0.15f) / 0.30f);
                if (v <= 0.75f) return Lerp(_cOrange, _cRed, (v - 0.45f) / 0.30f);
                return _cRed;
            }

            private static Color Lerp(Color a, Color b, float t)
            {
                if (t < 0) t = 0;
                if (t > 1) t = 1;

                return new Color(
                    a.Red + (b.Red - a.Red) * t,
                    a.Green + (b.Green - a.Green) * t,
                    a.Blue + (b.Blue - a.Blue) * t,
                    1f);
            }
        }
    }
}
