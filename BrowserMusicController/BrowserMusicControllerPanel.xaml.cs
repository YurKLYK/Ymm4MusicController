using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;

namespace BrowserMusicController
{
    /// <summary>
    /// ブラウザ音楽コントローラーのUIパネル
    /// YMM4のメインウィンドウにドッキングされます
    /// </summary>
    public partial class BrowserMusicControllerPanel : UserControl
    {
        private enum HostThemeTone
        {
            Light,
            Dark,
            Black,
        }

        private sealed class SessionEntry
        {
            public required string SourceAppId { get; init; }
            public required string DisplayName { get; init; }
            public required GlobalSystemMediaTransportControlsSession Session { get; init; }
        }

        private bool isSeekingManually;
        private bool isUpdatingSeekFromSession;
        private bool isPlaying;
        private TimeSpan currentDuration = TimeSpan.Zero;
        private bool isSessionInitialized;
        private GlobalSystemMediaTransportControlsSessionManager? sessionManager;
        private GlobalSystemMediaTransportControlsSession? currentSession;
        private GlobalSystemMediaTransportControlsSession? subscribedSession;
        private readonly List<SessionEntry> sessionEntries = [];
        private string? selectedSourceAppId;
        private bool isUpdatingSelector;
        private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
        private readonly DispatcherTimer waveTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private int metadataRefreshCounter;
        private DateTime lastTimelinePollUtc = DateTime.MinValue;
        private TimeSpan timelineElapsedSnapshot = TimeSpan.Zero;
        private DateTimeOffset timelineSnapshotAt = DateTimeOffset.MinValue;
        private bool hasTimelineSnapshot;
        private readonly List<Rectangle> waveBars = [];
        private SolidColorBrush waveBrush = new(Color.FromArgb(245, 255, 255, 255));
        private Color accentColor = Color.FromArgb(255, 239, 157, 38);
        private SolidColorBrush? _seekProgressBrush;
        private double wavePhase;
        private WasapiLoopbackCapture? loopbackCapture;
        private volatile float audioLevel;
        private volatile float bassLevel;
        private double bassFilterState;
        private bool isLightThemeActive;
        private HostThemeTone hostThemeTone = HostThemeTone.Dark;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private string _lastNotifiedTitle = string.Empty;
        private ulong _lastThumbnailSize;

        private const string GlyphPlay = "\uE768";
        private const string GlyphPause = "\uE769";

        // SendInputでシステム全体にメディアキーを送る
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
        private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
        private const ushort VK_MEDIA_STOP = 0xB2;
        private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public BrowserMusicControllerPanel()
        {
            InitializeComponent();
            refreshTimer.Tick += RefreshTimer_Tick;
            waveTimer.Tick += WaveTimer_Tick;
            Loaded += BrowserMusicControllerPanel_Loaded;
            Unloaded += BrowserMusicControllerPanel_Unloaded;
            SizeChanged += BrowserMusicControllerPanel_SizeChanged;
            UpdateStatus("初期化完了");
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return Dispatcher.InvokeAsync(action).Task;
        }

        private Task RunOnUiThreadAsync(Func<Task> action)
        {
            if (Dispatcher.CheckAccess())
                return action();

            return Dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        private async void BrowserMusicControllerPanel_Loaded(object sender, RoutedEventArgs e)
        {
            // Grid.Resources の SeekProgressBrush への参照を保持（DynamicResource は子から親へ探索するため code-behind からは Resources[] で見えない）
            if (Content is FrameworkElement root && root.Resources["SeekProgressBrush"] is SolidColorBrush sb)
                _seekProgressBrush = sb;

            ApplyThemeFromHost();
            await EnsureMediaSessionAsync();
            if (string.IsNullOrEmpty(selectedSourceAppId))
                selectedSourceAppId = "firefox";
            await RefreshSessionListAsync();
            await UpdateSessionInfoAsync();
            refreshTimer.Start();
            BuildWaveBars();
            if (WaveToggleButton.IsChecked == true)
            {
                StartAudioCapture();
                waveTimer.Start();
            }
        }

        private void BrowserMusicControllerPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            refreshTimer.Stop();
            waveTimer.Stop();
            StopAudioCapture();
            DetachCurrentSessionEvents();
            if (sessionManager != null)
            {
                sessionManager.SessionsChanged -= SessionManager_SessionsChanged;
                sessionManager.CurrentSessionChanged -= SessionManager_CurrentSessionChanged;
            }
            _initLock.Dispose();
        }

        private void BrowserMusicControllerPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            BuildWaveBars();
        }

        private async void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            await RefreshPlaybackStateAsync();

            // 一部ブラウザ/アプリで MediaPropertiesChanged が飛ばない場合があるため、定期的に情報を取り直す
            metadataRefreshCounter++;
            if (metadataRefreshCounter >= 16)
            {
                metadataRefreshCounter = 0;
                await UpdateSessionInfoAsync();
            }
        }

        private void WaveTimer_Tick(object? sender, EventArgs e)
        {
            UpdateWaveBars();
        }

        private async void RefreshSessionsButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSessionListAsync();
            await UpdateSessionInfoAsync();
        }

        private async void SessionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isUpdatingSelector)
                return;

            if (SessionSelector.SelectedItem is SessionEntry selected)
            {
                selectedSourceAppId = selected.SourceAppId;
                SetCurrentSession(selected.Session);
                await UpdateSessionInfoAsync();
            }
        }

        private void WaveToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            WaveCanvas.Visibility = Visibility.Visible;
            BuildWaveBars();
            StartAudioCapture();
            waveTimer.Start();
        }

        private void WaveToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            waveTimer.Stop();
            StopAudioCapture();
            WaveCanvas.Visibility = Visibility.Collapsed;
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            var ok = await TryControlSessionAsync(session => session.TryTogglePlayPauseAsync().AsTask());
            if (ok)
                UpdateStatus("再生/一時停止を送信");
            else
            {
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                UpdateStatus("再生/一時停止を送信（メディアキー経由）");
            }
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            var ok = await TryControlSessionAsync(session => session.TrySkipNextAsync().AsTask());
            if (ok)
                UpdateStatus("次へを送信");
            else
            {
                SendMediaKey(VK_MEDIA_NEXT_TRACK);
                UpdateStatus("次へを送信（メディアキー経由）");
            }
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            var ok = await TryControlSessionAsync(session => session.TrySkipPreviousAsync().AsTask());
            if (ok)
                UpdateStatus("前へを送信");
            else
            {
                SendMediaKey(VK_MEDIA_PREV_TRACK);
                UpdateStatus("前へを送信（メディアキー経由）");
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            var ok = await TryControlSessionAsync(session => session.TryStopAsync().AsTask());
            if (ok)
                UpdateStatus("停止を送信");
            else
            {
                SendMediaKey(VK_MEDIA_STOP);
                UpdateStatus("停止を送信（メディアキー経由）");
            }
        }

        private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isSeekingManually = true;
        }

        private async void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            isSeekingManually = false;
            await SeekToSliderPositionAsync();
        }

        private void SendMediaKey(ushort virtualKey)
        {
            try
            {
                var inputs = new[]
                {
                    new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        U = new InputUnion
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = virtualKey,
                                wScan = 0,
                                dwFlags = 0,
                                time = 0,
                                dwExtraInfo = IntPtr.Zero,
                            }
                        }
                    },
                    new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        U = new InputUnion
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = virtualKey,
                                wScan = 0,
                                dwFlags = KEYEVENTF_KEYUP,
                                time = 0,
                                dwExtraInfo = IntPtr.Zero,
                            }
                        }
                    }
                };

                var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
                if (sent != inputs.Length)
                {
                    UpdateStatus("メディアキー送信に失敗しました");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"エラー: {ex.Message}");
            }
        }

        private async Task EnsureMediaSessionAsync()
        {
            if (isSessionInitialized)
                return;

            await _initLock.WaitAsync();
            try
            {
                if (isSessionInitialized)
                    return;

                sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                sessionManager.SessionsChanged += SessionManager_SessionsChanged;
                sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;
                await RefreshSessionListAsync();
                SetCurrentSession(GetPreferredSession());
                isSessionInitialized = true;
            }
            catch
            {
                // 取得失敗時はメディアキー送信フォールバックのみで動作
                isSessionInitialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        private async void SessionManager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            await RunOnUiThreadAsync(async () =>
            {
                await RefreshSessionListAsync();
                SetCurrentSession(GetPreferredSession());
                await UpdateSessionInfoAsync();
            });
        }

        private async void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            await RunOnUiThreadAsync(async () =>
            {
                await RefreshSessionListAsync();
                SetCurrentSession(GetPreferredSession());
                await UpdateSessionInfoAsync();
            });
        }

        private async void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RunOnUiThreadAsync(() =>
            {
                _lastThumbnailSize = 0; // 曲変更時は強制再取得（同サイズサムネでスキップされるのを防止）
                return UpdateSessionInfoAsync();
            });
        }

        private async void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            await RunOnUiThreadAsync(RefreshPlaybackStateAsync);
        }

        private async void CurrentSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            await RunOnUiThreadAsync(RefreshPlaybackStateAsync);
        }

        private async Task UpdateSessionInfoAsync()
        {
            if (currentSession == null)
            {
                UpdateStatus("アクティブなメディアセッションを待機中...");
                TrackTitleText.Text = "再生中のメディアを待機中";
                ArtistText.Text = "Spotify / YouTube など";
                ElapsedTimeText.Text = "0:00";
                TimeDisplay.Text = "0:00";
                SeekSlider.Value = 0;
                UpdatePlaybackVisualState(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped);
                return;
            }

            try
            {
                var props = await currentSession.TryGetMediaPropertiesAsync();
                if (!string.IsNullOrEmpty(props.Title))
                {
                    TrackTitleText.Text = props.Title;
                    ArtistText.Text = string.IsNullOrWhiteSpace(props.Artist) ? "不明なアーティスト" : props.Artist;
                    UpdateStatus($"接続中: {props.Title}");

                    // 曲が変わったらトースト通知
                    if (props.Title != _lastNotifiedTitle)
                    {
                        _lastNotifiedTitle = props.Title;
                        ShowTrackToast(props.Title, props.Artist ?? "");
                    }
                }
                else
                {
                    TrackTitleText.Text = "再生中のメディアを検出";
                    ArtistText.Text = "タイトル情報なし";
                    UpdateStatus("メディアセッションに接続");
                }

                await UpdateThumbnailAsync(props.Thumbnail);
                await RefreshPlaybackStateAsync();
            }
            catch
            {
                TrackTitleText.Text = "メディア情報の取得に失敗";
                ArtistText.Text = "再生中アプリの切替後に再試行してください";
                UpdateStatus("メディアセッションに接続");
            }
        }

        private async Task RefreshPlaybackStateAsync()
        {
            await EnsureMediaSessionAsync();

            if (currentSession == null)
                SetCurrentSession(GetPreferredSession());

            if (currentSession == null)
            {
                UpdatePlaybackVisualState(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped);
                ElapsedTimeText.Text = "0:00";
                TimeDisplay.Text = "0:00";
                if (!isSeekingManually)
                    SeekSlider.Value = 0;
                hasTimelineSnapshot = false;
                return;
            }

            try
            {
                var nowUtc = DateTime.UtcNow;
                var needsTimelinePoll = !hasTimelineSnapshot || (nowUtc - lastTimelinePollUtc) >= TimeSpan.FromMilliseconds(420);

                if (needsTimelinePoll)
                {
                    var timeline = currentSession.GetTimelineProperties();
                    var playback = currentSession.GetPlaybackInfo();
                    UpdatePlaybackVisualState(playback.PlaybackStatus);

                    var start = timeline.StartTime;
                    var end = timeline.EndTime;
                    var position = timeline.Position;

                    if (end > start)
                    {
                        currentDuration = end - start;
                        var elapsed = position - start;
                        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                        if (elapsed > currentDuration) elapsed = currentDuration;

                        timelineElapsedSnapshot = elapsed;
                        timelineSnapshotAt = timeline.LastUpdatedTime == default ? DateTimeOffset.UtcNow : timeline.LastUpdatedTime;
                        hasTimelineSnapshot = true;
                        lastTimelinePollUtc = nowUtc;
                    }
                }

                if (!hasTimelineSnapshot || currentDuration <= TimeSpan.Zero)
                    return;

                var predictedElapsed = timelineElapsedSnapshot;
                if (isPlaying)
                {
                    var delta = DateTimeOffset.UtcNow - timelineSnapshotAt;
                    if (delta > TimeSpan.Zero)
                        predictedElapsed += delta;
                }

                if (predictedElapsed < TimeSpan.Zero) predictedElapsed = TimeSpan.Zero;
                if (predictedElapsed > currentDuration) predictedElapsed = currentDuration;

                ElapsedTimeText.Text = FormatTime(predictedElapsed);
                TimeDisplay.Text = FormatTime(currentDuration);

                if (!isSeekingManually && !isUpdatingSeekFromSession)
                {
                    isUpdatingSeekFromSession = true;
                    SeekSlider.Value = currentDuration.TotalMilliseconds <= 0
                        ? 0
                        : predictedElapsed.TotalMilliseconds / currentDuration.TotalMilliseconds * 100.0;
                    isUpdatingSeekFromSession = false;
                }
            }
            catch
            {
            }
        }

        private async Task SeekToSliderPositionAsync()
        {
            if (currentSession == null)
                SetCurrentSession(GetPreferredSession());

            if (currentSession == null || currentDuration <= TimeSpan.Zero)
            {
                UpdateStatus("シーク対象がありません");
                return;
            }

            try
            {
                var timeline = currentSession.GetTimelineProperties();
                var ratio = Math.Clamp(SeekSlider.Value / 100.0, 0.0, 1.0);
                var target = TimeSpan.FromMilliseconds(currentDuration.TotalMilliseconds * ratio);

                var ok = await currentSession.TryChangePlaybackPositionAsync(target.Ticks);
                if (ok)
                {
                    timelineElapsedSnapshot = target;
                    timelineSnapshotAt = DateTimeOffset.UtcNow;
                    hasTimelineSnapshot = true;
                    lastTimelinePollUtc = DateTime.UtcNow;
                    UpdateStatus($"シーク: {(int)(ratio * 100)}%");
                }
                else
                    UpdateStatus("シーク操作に失敗しました");
            }
            catch
            {
                UpdateStatus("シーク操作に失敗しました");
            }
        }

        private void UpdatePlaybackVisualState(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            switch (status)
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                    isPlaying = true;
                    PlayPauseButton.Content = GlyphPause;
                    PlayPauseButton.Background = new SolidColorBrush(Color.FromArgb(230, accentColor.R, accentColor.G, accentColor.B));
                    HeaderText.Text = "再生中 / Browser Media";
                    break;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    isPlaying = false;
                    PlayPauseButton.Content = GlyphPlay;
                    PlayPauseButton.Background = isLightThemeActive
                        ? new SolidColorBrush(Color.FromArgb(220, 223, 231, 242))
                        : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
                    HeaderText.Text = "一時停止中 / Browser Media";
                    break;
                default:
                    isPlaying = false;
                    PlayPauseButton.Content = GlyphPlay;
                    PlayPauseButton.Background = isLightThemeActive
                        ? new SolidColorBrush(Color.FromArgb(220, 223, 231, 242))
                        : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
                    HeaderText.Text = "停止中 / Browser Media";
                    break;
            }
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";

            return $"{time.Minutes}:{time.Seconds:00}";
        }


        private static void ShowTrackToast(string title, string artist)
        {
            try
            {
                var xml = new XmlDocument();
                var artistLine = string.IsNullOrWhiteSpace(artist) ? "" : $"<text>{SecurityEncodeXml(artist)}</text>";
                xml.LoadXml($@"<toast duration=""short"">
                    <visual><binding template=""ToastGeneric"">
                        <text>&#127925; {SecurityEncodeXml(title)}</text>
                        {artistLine}
                    </binding></visual>
                </toast>");
                var notifier = ToastNotificationManager.CreateToastNotifier("BrowserMusicController");
                notifier.Show(new ToastNotification(xml));
            }
            catch
            {
                // トースト非対応環境では無視
            }
        }

        private static string SecurityEncodeXml(string text)
            => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private GlobalSystemMediaTransportControlsSession? GetPreferredSession()
        {
            if (sessionEntries.Count > 0)
            {
                if (!string.IsNullOrEmpty(selectedSourceAppId))
                {
                    var manual = sessionEntries.FirstOrDefault(x => x.SourceAppId.Equals(selectedSourceAppId, StringComparison.OrdinalIgnoreCase));
                    if (manual != null)
                        return manual.Session;
                }

                var firefoxPlaying = sessionEntries.FirstOrDefault(x =>
                    x.SourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase) &&
                    x.Session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                if (firefoxPlaying != null)
                    return firefoxPlaying.Session;

                var firefoxPaused = sessionEntries.FirstOrDefault(x =>
                    x.SourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase) &&
                    x.Session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
                if (firefoxPaused != null)
                    return firefoxPaused.Session;
            }

            if (sessionManager == null)
                return null;

            var sessions = sessionManager.GetSessions();
            if (sessions == null || sessions.Count == 0)
                return sessionManager.GetCurrentSession();

            var byPriority = sessions
                .Select(s => new
                {
                    Session = s,
                    Playback = s.GetPlaybackInfo().PlaybackStatus,
                    Source = s.SourceAppUserModelId ?? string.Empty,
                })
                .OrderByDescending(x => x.Playback == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                .ThenByDescending(x => x.Playback == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                .ThenByDescending(x =>
                    x.Source.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
                    x.Source.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                    x.Source.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                    x.Source.Contains("firefox", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Session)
                .FirstOrDefault();

            return byPriority ?? sessionManager.GetCurrentSession();
        }

        private async Task RefreshSessionListAsync()
        {
            if (!Dispatcher.CheckAccess())
            {
                await RunOnUiThreadAsync(RefreshSessionListAsync);
                return;
            }

            if (sessionManager == null)
                return;

            sessionEntries.Clear();
            var sessions = sessionManager.GetSessions();

            foreach (var session in sessions)
            {
                var source = session.SourceAppUserModelId ?? "Unknown";
                string title = string.Empty;

                try
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    title = props.Title ?? string.Empty;
                }
                catch
                {
                }

                var status = session.GetPlaybackInfo().PlaybackStatus;
                var display = string.IsNullOrWhiteSpace(title)
                    ? $"{source} ({status})"
                    : $"{title} - {source} ({status})";

                sessionEntries.Add(new SessionEntry
                {
                    SourceAppId = source,
                    DisplayName = display,
                    Session = session,
                });
            }

            isUpdatingSelector = true;
            SessionSelector.ItemsSource = null;
            SessionSelector.ItemsSource = sessionEntries;
            SessionSelector.DisplayMemberPath = nameof(SessionEntry.DisplayName);

            var selected = sessionEntries.FirstOrDefault(x =>
                !string.IsNullOrEmpty(selectedSourceAppId) &&
                x.SourceAppId.Equals(selectedSourceAppId, StringComparison.OrdinalIgnoreCase));

            if (selected == null)
            {
                var preferred = GetPreferredSession();
                selected = sessionEntries.FirstOrDefault(x => ReferenceEquals(x.Session, preferred));
            }

            if (selected != null)
            {
                SessionSelector.SelectedItem = selected;
                selectedSourceAppId = selected.SourceAppId;
            }
            else if (sessionEntries.Count > 0)
            {
                SessionSelector.SelectedIndex = 0;
                selectedSourceAppId = sessionEntries[0].SourceAppId;
            }

            isUpdatingSelector = false;
        }

        private void SetCurrentSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (ReferenceEquals(currentSession, session))
                return;

            DetachCurrentSessionEvents();
            currentSession = session;

            if (currentSession != null)
            {
                currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;
                subscribedSession = currentSession;
            }
        }

        private void DetachCurrentSessionEvents()
        {
            if (subscribedSession == null)
                return;

            subscribedSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
            subscribedSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
            subscribedSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
            subscribedSession = null;
        }

        private async Task UpdateThumbnailAsync(IRandomAccessStreamReference? thumbnail)
        {
            if (thumbnail == null)
            {
                _lastThumbnailSize = 0;
                ThumbnailImage.Source = null;
                AlbumBackgroundImage.Source = null;
                return;
            }

            try
            {
                using var stream = await thumbnail.OpenReadAsync();
                if (stream.Size == 0 || stream.Size > int.MaxValue)
                    return;

                // サイズが同じなら同じサムネ → フェードをスキップ
                if (stream.Size == _lastThumbnailSize && ThumbnailImage.Source != null)
                    return;

                using var inputStream = stream.GetInputStreamAt(0);
                using var reader = new DataReader(inputStream);
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[(int)stream.Size];
                reader.ReadBytes(bytes);
                _lastThumbnailSize = stream.Size;

                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();
                }

                // ThumbnailImage・AlbumBackgroundImage 両方をフェードアウト → ソース切り替え → フェードイン
                var albumBgTargetOpacity = AlbumBackgroundImage.Opacity;
                var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
                fadeOut.Completed += (_, _) =>
                {
                    ThumbnailImage.Source = image;
                    AlbumBackgroundImage.Source = image;
                    ThumbnailImage.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
                    AlbumBackgroundImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, albumBgTargetOpacity, TimeSpan.FromMilliseconds(220)));
                };
                ThumbnailImage.BeginAnimation(OpacityProperty, fadeOut);
                AlbumBackgroundImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));

                // アルバムアートの主要色を波形に反映
                ApplyDominantColorToWave(image);
            }
            catch
            {
            }
        }

        private void ApplyDominantColorToWave(BitmapImage image)
        {
            try
            {
                // サムネを小さくスケールして高速にサンプリング
                var scaled = new TransformedBitmap(image, new ScaleTransform(16.0 / image.PixelWidth, 16.0 / image.PixelHeight));
                var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Bgr32, null, 0);
                formatted.Freeze();

                var stride = formatted.PixelWidth * 4;
                var pixels = new byte[formatted.PixelHeight * stride];
                formatted.CopyPixels(pixels, stride, 0);

                long r = 0, g = 0, b = 0;
                var count = pixels.Length / 4;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    b += pixels[i];
                    g += pixels[i + 1];
                    r += pixels[i + 2];
                }

                var ar = (byte)Math.Clamp(r / count, 0, 255);
                var ag = (byte)Math.Clamp(g / count, 0, 255);
                var ab = (byte)Math.Clamp(b / count, 0, 255);

                // HSV彩度を計算 — 白/グレー系アルバムアートで波形が白いモヤにならないように
                var max = Math.Max(Math.Max(ar, ag), ab);
                var min = Math.Min(Math.Min(ar, ag), ab);
                var saturation = max > 0 ? (double)(max - min) / max : 0.0;

                // 彩度が低すぎる場合はwaveBrushを更新せず前の色を維持（白いモヤ防止）
                if (saturation < 0.18)
                    return;

                // 彩度を上げて波形に映えるようにブースト
                var factor = max > 30 ? Math.Min(255.0 / max, 2.2) : 1.0;
                ar = (byte)Math.Clamp(ar * factor, 0, 255);
                ag = (byte)Math.Clamp(ag * factor, 0, 255);
                ab = (byte)Math.Clamp(ab * factor, 0, 255);

                accentColor = Color.FromRgb(ar, ag, ab);
                waveBrush = new SolidColorBrush(Color.FromArgb(230, ar, ag, ab));

                // 波形バーの色を更新
                foreach (var bar in waveBars)
                    bar.Fill = waveBrush;

                // ボタン・シークバーにもアクセントカラーを適用
                ApplyAccentColorToUI();
            }
            catch
            {
                // 失敗しても波形色はデフォルトのまま
            }
        }

        private void ApplyAccentColorToUI()
        {
            var c = accentColor;
            // シークバーの進捗色
            if (_seekProgressBrush != null)
                _seekProgressBrush.Color = c;

            // ボタン背景 — 半透明アクセントカラー
            var bg = Color.FromArgb(118, c.R, c.G, c.B);
            var border = Color.FromArgb(175, c.R, c.G, c.B);
            PrevButton.Background = new SolidColorBrush(bg);
            PrevButton.BorderBrush = new SolidColorBrush(border);
            NextButton.Background = new SolidColorBrush(bg);
            NextButton.BorderBrush = new SolidColorBrush(border);
            StopButton.Background = new SolidColorBrush(bg);
            StopButton.BorderBrush = new SolidColorBrush(border);
            WaveToggleButton.Background = new SolidColorBrush(bg);
            WaveToggleButton.BorderBrush = new SolidColorBrush(border);

            if (isPlaying)
                PlayPauseButton.Background = new SolidColorBrush(Color.FromArgb(230, c.R, c.G, c.B));
        }

        private void ApplyThemeFromHost()
        {
            hostThemeTone = DetectHostThemeTone();
            isLightThemeActive = hostThemeTone == HostThemeTone.Light;

            if (isLightThemeActive)
            {
                AlbumBackgroundImage.Opacity = 0.66;
                BackgroundShade.Fill = new SolidColorBrush(Color.FromArgb(68, 244, 247, 253));
                MainCard.Background = new SolidColorBrush(Color.FromArgb(166, 252, 254, 255));
                MainCard.BorderBrush = new SolidColorBrush(Color.FromArgb(124, 82, 94, 113));
                if (ThumbnailBorder != null)
                {
                    ThumbnailBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(112, 79, 90, 109));
                    ThumbnailBorder.Background = new SolidColorBrush(Color.FromArgb(72, 189, 201, 222));
                }

                HeaderText.Foreground = new SolidColorBrush(Color.FromArgb(220, 39, 53, 74));
                TrackTitleText.Foreground = new SolidColorBrush(Color.FromArgb(255, 21, 31, 48));
                ArtistText.Foreground = new SolidColorBrush(Color.FromArgb(232, 50, 62, 82));
                SessionLabelText.Foreground = new SolidColorBrush(Color.FromArgb(225, 45, 59, 82));
                ElapsedTimeText.Foreground = new SolidColorBrush(Color.FromArgb(225, 53, 67, 90));
                TimeDisplay.Foreground = new SolidColorBrush(Color.FromArgb(225, 53, 67, 90));
                StatusText.Foreground = new SolidColorBrush(Color.FromArgb(210, 67, 81, 103));

                SessionSelector.Background = new SolidColorBrush(Color.FromArgb(232, 246, 250, 255));
                SessionSelector.Foreground = new SolidColorBrush(Color.FromArgb(255, 29, 40, 60));
                SessionSelector.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 95, 108, 129));

                RefreshSessionsMiniButton.Background = new SolidColorBrush(Color.FromArgb(218, 235, 241, 251));
                RefreshSessionsMiniButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 31, 44, 67));
                RefreshSessionsMiniButton.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 88, 101, 124));

                PrevButton.Background = new SolidColorBrush(Color.FromArgb(220, 227, 233, 244));
                PrevButton.BorderBrush = new SolidColorBrush(Color.FromArgb(138, 102, 113, 130));
                PrevButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 40, 54, 74));

                PlayPauseButton.BorderBrush = new SolidColorBrush(Color.FromArgb(150, 100, 110, 126));
                PlayPauseButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

                NextButton.Background = new SolidColorBrush(Color.FromArgb(220, 227, 233, 244));
                NextButton.BorderBrush = new SolidColorBrush(Color.FromArgb(138, 102, 113, 130));
                NextButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 40, 54, 74));

                StopButton.Background = new SolidColorBrush(Color.FromArgb(220, 227, 233, 244));
                StopButton.BorderBrush = new SolidColorBrush(Color.FromArgb(138, 102, 113, 130));
                StopButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 40, 54, 74));

                WaveToggleButton.Background = new SolidColorBrush(Color.FromArgb(214, 236, 242, 252));
                WaveToggleButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 33, 46, 66));
                WaveToggleButton.BorderBrush = new SolidColorBrush(Color.FromArgb(138, 98, 111, 132));

                waveBrush = new SolidColorBrush(Color.FromArgb(238, 47, 62, 88));
            }
            else if (hostThemeTone == HostThemeTone.Black)
            {
                AlbumBackgroundImage.Opacity = 0.64;
                BackgroundShade.Fill = new SolidColorBrush(Color.FromArgb(118, 4, 5, 8));
                MainCard.Background = new SolidColorBrush(Color.FromArgb(122, 10, 13, 20));
                MainCard.BorderBrush = new SolidColorBrush(Color.FromArgb(118, 248, 251, 255));
                if (ThumbnailBorder != null)
                {
                    ThumbnailBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(118, 250, 252, 255));
                    ThumbnailBorder.Background = new SolidColorBrush(Color.FromArgb(44, 34, 40, 52));
                }

                HeaderText.Foreground = new SolidColorBrush(Color.FromArgb(228, 242, 247, 255));
                TrackTitleText.Foreground = new SolidColorBrush(Color.FromArgb(255, 249, 251, 255));
                ArtistText.Foreground = new SolidColorBrush(Color.FromArgb(218, 238, 244, 255));
                SessionLabelText.Foreground = new SolidColorBrush(Color.FromArgb(214, 236, 242, 255));
                ElapsedTimeText.Foreground = new SolidColorBrush(Color.FromArgb(214, 237, 243, 255));
                TimeDisplay.Foreground = new SolidColorBrush(Color.FromArgb(214, 237, 243, 255));
                StatusText.Foreground = new SolidColorBrush(Color.FromArgb(188, 230, 237, 250));

                SessionSelector.Background = new SolidColorBrush(Color.FromArgb(198, 22, 28, 40));
                SessionSelector.Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 248, 255));
                SessionSelector.BorderBrush = new SolidColorBrush(Color.FromArgb(124, 245, 250, 255));

                RefreshSessionsMiniButton.Background = new SolidColorBrush(Color.FromArgb(110, 240, 246, 255));
                RefreshSessionsMiniButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 244, 247, 255));
                RefreshSessionsMiniButton.BorderBrush = new SolidColorBrush(Color.FromArgb(122, 246, 250, 255));

                PrevButton.Background = new SolidColorBrush(Color.FromArgb(86, 244, 249, 255));
                PrevButton.BorderBrush = new SolidColorBrush(Color.FromArgb(122, 246, 250, 255));
                PrevButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 246, 249, 255));

                PlayPauseButton.BorderBrush = new SolidColorBrush(Color.FromArgb(132, 245, 249, 255));
                PlayPauseButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 247, 250, 255));

                NextButton.Background = new SolidColorBrush(Color.FromArgb(86, 244, 249, 255));
                NextButton.BorderBrush = new SolidColorBrush(Color.FromArgb(122, 246, 250, 255));
                NextButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 246, 249, 255));

                StopButton.Background = new SolidColorBrush(Color.FromArgb(86, 244, 249, 255));
                StopButton.BorderBrush = new SolidColorBrush(Color.FromArgb(122, 246, 250, 255));
                StopButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 246, 249, 255));

                WaveToggleButton.Background = new SolidColorBrush(Color.FromArgb(96, 240, 247, 255));
                WaveToggleButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 248, 255));
                WaveToggleButton.BorderBrush = new SolidColorBrush(Color.FromArgb(122, 246, 250, 255));

                waveBrush = new SolidColorBrush(Color.FromArgb(236, 246, 250, 255));
            }
            else
            {
                AlbumBackgroundImage.Opacity = 0.60;
                BackgroundShade.Fill = new SolidColorBrush(Color.FromArgb(140, 8, 10, 14));
                MainCard.Background = new SolidColorBrush(Color.FromArgb(136, 18, 21, 30));
                MainCard.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
                if (ThumbnailBorder != null)
                {
                    ThumbnailBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255));
                    ThumbnailBorder.Background = new SolidColorBrush(Color.FromArgb(51, 42, 46, 55));
                }

                HeaderText.Foreground = new SolidColorBrush(Color.FromArgb(216, 255, 255, 255));
                TrackTitleText.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 249, 250));
                ArtistText.Foreground = new SolidColorBrush(Color.FromArgb(208, 255, 255, 255));
                SessionLabelText.Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
                ElapsedTimeText.Foreground = new SolidColorBrush(Color.FromArgb(214, 255, 255, 255));
                TimeDisplay.Foreground = new SolidColorBrush(Color.FromArgb(214, 255, 255, 255));
                StatusText.Foreground = new SolidColorBrush(Color.FromArgb(166, 255, 255, 255));

                SessionSelector.Background = new SolidColorBrush(Color.FromArgb(255, 44, 50, 62));
                SessionSelector.Foreground = new SolidColorBrush(Color.FromArgb(255, 242, 245, 249));
                SessionSelector.BorderBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));

                RefreshSessionsMiniButton.Background = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));
                RefreshSessionsMiniButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 247, 248, 250));
                RefreshSessionsMiniButton.BorderBrush = new SolidColorBrush(Color.FromArgb(127, 255, 255, 255));

                PrevButton.Background = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255));
                PrevButton.BorderBrush = new SolidColorBrush(Color.FromArgb(127, 255, 255, 255));
                PrevButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 247, 248, 250));

                PlayPauseButton.BorderBrush = new SolidColorBrush(Color.FromArgb(127, 255, 255, 255));
                PlayPauseButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 247, 248, 250));

                NextButton.Background = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255));
                NextButton.BorderBrush = new SolidColorBrush(Color.FromArgb(127, 255, 255, 255));
                NextButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 247, 248, 250));

                StopButton.Background = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255));
                StopButton.BorderBrush = new SolidColorBrush(Color.FromArgb(127, 255, 255, 255));
                StopButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 247, 248, 250));

                WaveToggleButton.Background = new SolidColorBrush(Color.FromArgb(108, 255, 255, 255));
                WaveToggleButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 244, 246, 250));
                WaveToggleButton.BorderBrush = new SolidColorBrush(Color.FromArgb(118, 255, 255, 255));

                waveBrush = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255));
            }

            if (!isPlaying)
            {
                PlayPauseButton.Background = isLightThemeActive
                    ? new SolidColorBrush(Color.FromArgb(220, 223, 231, 242))
                    : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            }

            foreach (var bar in waveBars)
                bar.Fill = waveBrush;
        }

        private void BuildWaveBars()
        {
            if (WaveCanvas == null)
                return;

            WaveCanvas.Children.Clear();
            waveBars.Clear();

            var width = WaveCanvas.ActualWidth;
            if (width <= 0)
                width = 520;

            const double barWidth = 5;
            const double spacing = 4;
            var count = Math.Max(18, (int)((width + spacing) / (barWidth + spacing)));
            var totalWidth = count * barWidth + (count - 1) * spacing;
            var startX = Math.Max(0, (width - totalWidth) * 0.5);

            for (var i = 0; i < count; i++)
            {
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = 4,
                    RadiusX = 0,
                    RadiusY = 0,
                    Fill = waveBrush,
                };

                WaveCanvas.Children.Add(bar);
                waveBars.Add(bar);
                Canvas.SetLeft(bar, startX + i * (barWidth + spacing));
                Canvas.SetTop(bar, WaveCanvas.Height - 6);
            }
        }

        private void UpdateWaveBars()
        {
            if (WaveToggleButton.IsChecked != true || waveBars.Count == 0)
                return;

            var height = WaveCanvas.Height;
            if (height <= 0)
                return;

            var center = (waveBars.Count - 1) / 2.0;
            var activeGain = isPlaying ? 1.0 : 0.24;
            var meter = Math.Clamp((double)audioLevel, 0.0, 1.0);
            var bass = Math.Clamp((double)bassLevel, 0.0, 1.0);
            var reactiveGain = isPlaying ? (0.18 + meter * 0.95 + bass * 1.55) : 0.2;
            var maxAmp = 82 * activeGain * reactiveGain;
            var maxBarHeight = Math.Max(10, height - 1);
            var softClipStart = Math.Max(8, maxBarHeight * 0.78);

            for (var i = 0; i < waveBars.Count; i++)
            {
                var distance = Math.Abs(i - center) / Math.Max(1, center);
                var envelope = Math.Exp(-3.2 * distance * distance);
                var oscillation = (Math.Sin(wavePhase + i * 0.43) + Math.Sin(wavePhase * 0.7 + i * 0.21)) * 0.5;
                var normalized = (oscillation + 1) * 0.5;
                var bassPulse = 1.0 + bass * envelope * 1.15;
                var rawHeight = 3 + envelope * maxAmp * (0.25 + 0.75 * normalized) * bassPulse;
                var h = SoftClipHeight(rawHeight, 2.0, softClipStart, maxBarHeight);

                var bar = waveBars[i];
                bar.Height = h;
                Canvas.SetTop(bar, height - h - 2);
            }

            // EDM寄りの速い躍動感を出すため位相進行を大きくする
            wavePhase += isPlaying ? (0.44 + meter * 0.72 + bass * 0.66) : 0.14;
        }

        private static double SoftClipHeight(double value, double min, double kneeStart, double max)
        {
            if (value <= kneeStart)
                return Math.Clamp(value, min, max);

            var kneeRange = Math.Max(0.001, max - kneeStart);
            var over = value - kneeStart;
            // 先に上限で切らずに圧縮することで、天井への張り付き感を抑える
            var compressed = kneeStart + kneeRange * (1.0 - Math.Exp(-(over / (kneeRange * 1.35))));
            return Math.Clamp(compressed, min, max);
        }

        private void StartAudioCapture()
        {
            if (loopbackCapture != null)
                return;

            try
            {
                loopbackCapture = new WasapiLoopbackCapture();
                loopbackCapture.DataAvailable += LoopbackCapture_DataAvailable;
                loopbackCapture.RecordingStopped += LoopbackCapture_RecordingStopped;
                loopbackCapture.StartRecording();
            }
            catch
            {
                audioLevel = 0f;
                bassLevel = 0f;
            }
        }

        private void StopAudioCapture()
        {
            if (loopbackCapture == null)
                return;

            try
            {
                loopbackCapture.DataAvailable -= LoopbackCapture_DataAvailable;
                loopbackCapture.RecordingStopped -= LoopbackCapture_RecordingStopped;
                loopbackCapture.StopRecording();
            }
            catch
            {
            }

            loopbackCapture.Dispose();
            loopbackCapture = null;
            audioLevel = 0f;
            bassLevel = 0f;
            bassFilterState = 0;
        }

        private void LoopbackCapture_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            audioLevel = 0f;
            bassLevel = 0f;
            bassFilterState = 0;
        }

        private void LoopbackCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            var capture = loopbackCapture;
            if (capture == null || e.BytesRecorded <= 0)
                return;

            var (level, lowBandLevel) = ComputeAudioLevels(e.Buffer, e.BytesRecorded, capture.WaveFormat, ref bassFilterState);
            // 低めの音量でも視覚的に反応するように少し持ち上げる
            var boosted = Math.Clamp(level * 4.6, 0.0, 1.0);
            // 低音はより強く持ち上げて、キックやベースで大きく動くようにする
            var boostedBass = Math.Clamp(lowBandLevel * 12.0, 0.0, 1.0);
            audioLevel = (float)(audioLevel * 0.80f + (float)boosted * 0.20f);
            bassLevel = (float)(bassLevel * 0.70f + (float)boostedBass * 0.30f);
        }

        private static (double fullBand, double lowBand) ComputeAudioLevels(byte[] buffer, int bytesRecorded, WaveFormat format, ref double lowPassState)
        {
            var bytesPerSample = format.BitsPerSample / 8;
            if (bytesPerSample <= 0)
                return (0, 0);

            var channels = Math.Max(1, format.Channels);
            var frameSize = bytesPerSample * channels;
            if (frameSize <= 0)
                return (0, 0);

            var frameCount = bytesRecorded / frameSize;
            if (frameCount <= 0)
                return (0, 0);

            var sampleRate = Math.Max(8000, format.SampleRate);
            const double bassCutoffHz = 180.0;
            var lowPassK = 1.0 - Math.Exp(-(2.0 * Math.PI * bassCutoffHz) / sampleRate);

            double fullSquares = 0;
            double lowSquares = 0;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var baseOffset = frame * frameSize;
                    double mono = 0;

                    for (var ch = 0; ch < channels; ch++)
                    {
                        mono += BitConverter.ToSingle(buffer, baseOffset + ch * 4);
                    }

                    mono /= channels;
                    fullSquares += mono * mono;

                    lowPassState += lowPassK * (mono - lowPassState);
                    lowSquares += lowPassState * lowPassState;
                }

                return (Math.Sqrt(fullSquares / frameCount), Math.Sqrt(lowSquares / frameCount));
            }

            if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var baseOffset = frame * frameSize;
                    double mono = 0;

                    for (var ch = 0; ch < channels; ch++)
                    {
                        mono += BitConverter.ToInt16(buffer, baseOffset + ch * 2) / 32768.0;
                    }

                    mono /= channels;
                    fullSquares += mono * mono;

                    lowPassState += lowPassK * (mono - lowPassState);
                    lowSquares += lowPassState * lowPassState;
                }

                return (Math.Sqrt(fullSquares / frameCount), Math.Sqrt(lowSquares / frameCount));
            }

            return (0, 0);
        }

        private HostThemeTone DetectHostThemeTone()
        {
            if (TryGetHostBackgroundColor(out var hostColor))
            {
                var luminance = GetLuminance(hostColor);
                if (luminance >= 0.62)
                    return HostThemeTone.Light;
                if (luminance <= 0.15)
                    return HostThemeTone.Black;

                return HostThemeTone.Dark;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme") as int?;
                return value == 1 ? HostThemeTone.Light : HostThemeTone.Dark;
            }
            catch
            {
                return HostThemeTone.Dark;
            }
        }

        private bool TryGetHostBackgroundColor(out Color color)
        {
            for (DependencyObject? node = this; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (TryGetBackgroundFromNode(node, out color))
                    return true;
            }

            var window = Window.GetWindow(this);
            if (window?.Background is SolidColorBrush brush)
            {
                color = brush.Color;
                if (color.A > 0)
                    return true;
            }

            color = default;
            return false;
        }

        private static bool TryGetBackgroundFromNode(DependencyObject node, out Color color)
        {
            if (node is Panel panel && panel.Background is SolidColorBrush panelBrush && panelBrush.Color.A > 10)
            {
                color = panelBrush.Color;
                return true;
            }

            if (node is Border border && border.Background is SolidColorBrush borderBrush && borderBrush.Color.A > 10)
            {
                color = borderBrush.Color;
                return true;
            }

            if (node is Control control && control.Background is SolidColorBrush controlBrush && controlBrush.Color.A > 10)
            {
                color = controlBrush.Color;
                return true;
            }

            color = default;
            return false;
        }

        private static double GetLuminance(Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        }

        private async Task<bool> TryControlSessionAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
        {
            await EnsureMediaSessionAsync();

            if (currentSession == null)
                SetCurrentSession(GetPreferredSession());

            if (currentSession != null)
            {
                try
                {
                    if (await action(currentSession))
                        return true;
                }
                catch
                {
                }
            }

            // セッションAPIで失敗した場合はシステムメディアキーでフォールバック
            return false;
        }

        private void UpdateStatus(string message)
        {
            if (StatusText != null)
                StatusText.Text = message;
        }
    }
}
