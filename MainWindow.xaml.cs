//#define CRASH_TEST

using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media; // Добавь это для иконок
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Geometry = System.Windows.Media.Geometry;
using System.Collections.ObjectModel; // Добавь в using
using System.Text.Json.Serialization; // Нужно добавить
using System.Runtime.InteropServices;
using System.Windows.Interop;

// Алиасы для устранения конфликтов имен
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ObsMonolithPlayer
{
    // Базовый класс
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(LocalTrack), typeDiscriminator: "local")]
    [JsonDerivedType(typeof(YouTubeTrack), typeDiscriminator: "youtube")]
    public abstract class PlaylistItem
    {
        public string Id { get; set; }
        public string Title { get; set; }

        public string OrderedBy { get; set; } // КТО ЗАКАЗАЛ

        // Теперь метод асинхронный и сам принимает папку кэша для проверки
        public abstract Task<string> ResolveMediaUriAsync(string cacheFolder);
    }

    // Локальный MP3
    public class LocalTrack : PlaylistItem
    {
        public LocalTrack() { }
        public LocalTrack(string path) { Id = path; Title = System.IO.Path.GetFileNameWithoutExtension(path); }

        public override Task<string> ResolveMediaUriAsync(string cacheFolder)
        {
            string cachedPath = Path.Combine(cacheFolder, Title + ".mp4");
            if (File.Exists(cachedPath)) return Task.FromResult(cachedPath); // Если есть клип - берем его

            return Task.FromResult(Id); // Иначе играем MP3
        }
    }

    // YouTube Трек
    public class YouTubeTrack : PlaylistItem
    {
        public YouTubeTrack() { }
        public YouTubeTrack(string videoId, string title) { Id = videoId; Title = title; }

        public override async Task<string> ResolveMediaUriAsync(string cacheFolder)
        {
            // На всякий случай проверяем: вдруг мы его уже скачивали
            string cachedPath = Path.Combine(cacheFolder, Title + ".mp4");
            if (File.Exists(cachedPath)) return cachedPath;

            string targetUrl = $"https://youtube.com/watch?v={Id}";

#if CRASH_TEST
        // Тестовый генератор хаоса: 50% шанс сломать ссылку для проверки логики
        if (new Random().Next(100) < 50)
        {
            targetUrl = "https://youtube.com/watch?v=broken_id_test_123";
            System.Diagnostics.Debug.WriteLine($"[TEST] URL искусственно поврежден: {targetUrl}");
        }
#endif

            return await YtDlpHelper.GetDirectStreamUrlAsync(targetUrl);
        }
    }
    public partial class MainWindow : Window
    {
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private List<string> _musicFiles = new();
        private Random _rng = new();
        private DispatcherTimer _timer;
        private PlayerConfig _config = new();
        private string _configPath = "player_settings.json";
        private bool _isUserSeeking = false;
        private long _pendingSeekTime = -1; // Время для восстановления
        private ObservableCollection<PlaylistItem> _sessionPlaylist = new();
        private PlaylistWindow _playlistWindow = null; // Ссылка на открытое окно
        private int _playlistIndex = -1;

        private string _libraryPath = @"C:\Users\vladh\OneDrive\OneDrive\музыка на случай Роскомнадзорпокалипсиса";
        private string _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoCache");

        // Внутри класса:
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Константы для регистрации
        private const uint VK_MEDIA_NEXTTRACK = 0xB0;
        private const uint VK_MEDIA_PREVTRACK = 0xB1;
        private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const int HOTKEY_ID = 9000; // Базовый ID для наших хоткеев

        public MainWindow()
        {
            InitializeComponent();
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC("--network-caching=3000", "--clock-jitter=500", "--clock-synchro=0");
            _mediaPlayer = new MediaPlayer(_libVLC);
            MainVideoView.MediaPlayer = _mediaPlayer;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Критически важно: подписка на Playing для восстановления позиции
            _mediaPlayer.Playing += (s, e) =>
            {
                if (_pendingSeekTime > 0)
                {
                    long timeToSeek = _pendingSeekTime;
                    _pendingSeekTime = -1;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_mediaPlayer.IsSeekable)
                        {
                            _mediaPlayer.Time = timeToSeek;

                            // ВОЗВРАЩАЕМ ГРОМКОСТЬ ПОСЛЕ ПРЫЖКА
                            _mediaPlayer.Volume = (int)sldVolume.Value;

                            if (_mediaPlayer.Length > 0)
                            {
                                sldProgress.Value = (double)timeToSeek / _mediaPlayer.Length;
                            }
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            };

            _mediaPlayer.Playing += (s, e) => Dispatcher.BeginInvoke(new Action(() => UpdatePlayPauseUI(true)));
            _mediaPlayer.Paused += (s, e) => Dispatcher.BeginInvoke(new Action(() => UpdatePlayPauseUI(false)));
            _mediaPlayer.Stopped += (s, e) => Dispatcher.BeginInvoke(new Action(() => UpdatePlayPauseUI(false)));

            _mediaPlayer.EndReached += (s, e) =>
            {
                // КРИТИЧНО: Не выполняем логику прямо здесь. 
                // Отправляем задачу в очередь, чтобы поток VLC мог спокойно завершиться.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    // Небольшая пауза (100мс) дает VLC время полностью освободить аудио-устройство
                    System.Threading.Thread.Sleep(100);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        PlayNext();
                    }));
                });
            };

            this.Loaded += (s, e) => {
                LoadSettings();
                StartPlayer();
            };
            this.Closing += (s, e) => SaveSettings();

            StartApiServer();
        }

        private void UpdatePlayPauseUI(bool isPlaying)
        {
            // 1. Обновляем основную кнопку
            btnPlayPause.Content = isPlaying ? "⏸" : "▶";

            // 2. Обновляем иконку в таскбаре (Windows Thumbnail)
            if (taskbarPlayGeometry != null)
            {
                // Геометрия Паузы: две полоски
                // Геометрия Плей: треугольник
                taskbarPlayGeometry.Geometry = isPlaying
                    ? Geometry.Parse("M0,0 H3 V10 H0 Z M6,0 H9 V10 H6 Z")
                    : Geometry.Parse("M0,0 L10,5 L0,10 Z");

                taskbarPlayPause.Description = isPlaying ? "Pause" : "Play";
            }
        }

        // Восполняет буфер до 10 будущих треков
        private void EnsureBufferIsFilled()
        {
            if (_musicFiles.Count == 0) return;

            // Пока разница между текущим индексом и концом списка меньше 11
            while (_sessionPlaylist.Count - 1 - _playlistIndex < 10)
            {
                string newTrackPath = GenerateNextLocalTrack();
                _sessionPlaylist.Add(new LocalTrack(newTrackPath));
            }

            UpdateNextTrackPreview();
        }

        // Изолированная логика рандома
        private string GenerateNextLocalTrack()
        {
            // Берем последние 10 добавленных путей, чтобы избежать частых повторов
            var recentPaths = _sessionPlaylist
                .TakeLast(10)
                .Select(t => t.Id)
                .ToList();

            var available = _musicFiles.Where(f => !recentPaths.Contains(f)).ToList();
            if (available.Count == 0) available = _musicFiles; // Защита от пустой выборки

            return available[_rng.Next(available.Count)];
        }

        private void UpdateNextTrackPreview()
        {
            Dispatcher.Invoke(() => {
                if (_playlistIndex >= 0 && _playlistIndex < _sessionPlaylist.Count - 1)
                {
                    // Берем следующий готовый трек из буфера
                    var nextTrack = _sessionPlaylist[_playlistIndex + 1];
                    txtNextTrack.Text = "Далее: " + nextTrack.Title;
                }
                else
                {
                    txtNextTrack.Text = "Далее: ---";
                }
            });
        }
        private void StartPlayer()
        {
            if (!Directory.Exists(_libraryPath)) Directory.CreateDirectory(_libraryPath);
            _musicFiles = Directory.GetFiles(_libraryPath, "*.mp3").ToList();

            LoadSettings();

            // Если история загружена и индекс валиден
            if (_playlistIndex >= 0 && _playlistIndex < _sessionPlaylist.Count)
            {
                PlayTrack(_sessionPlaylist[_playlistIndex]);
                // Время восстановится через событие Playing, которое уже есть в конструкторе
            }
            else
            {
                // Иначе начинаем с нуля
                _sessionPlaylist.Clear();
                _playlistIndex = -1;
                EnsureBufferIsFilled();
                PlayNext();
            }

            EnsureBufferIsFilled(); // Добиваем буфер до 10 треков вперед
        }

        private async void PlayTrack(PlaylistItem item)
        {
            // 1. Ставим интерфейс в режим загрузки
            Dispatcher.Invoke(() => {
                txtCurrentTrack.Text = "⏳ Обработка потока...";
                MainVideoView.Opacity = 0;
            });

            // 2. ДЕЛЕГИРУЕМ ПОИСК ССЫЛКИ САМОМУ ТРЕКУ
            string mediaUri = await item.ResolveMediaUriAsync(_cacheFolder);

            // Защита: если за время получения ссылки пользователь успел переключить трек дальше
            if (_sessionPlaylist.Count > _playlistIndex && _sessionPlaylist[_playlistIndex] != item)
                return;

            // Защита от удаленных видео
            if (string.IsNullOrEmpty(mediaUri))
            {
                Dispatcher.Invoke(() => {
                    txtCurrentTrack.Text = "❌ Ошибка потока. ▶ для повтора";
                    txtCurrentTrack.Foreground = Brushes.LightCoral;

                    // Очищаем медиа, чтобы функция TogglePlay поняла, что нужно сделать Retry
                    _mediaPlayer.Media = null;

                    // Переводим интерфейс в состояние "Пауза"
                    UpdatePlayPauseUI(false);
                    UpdateTaskbarIcon(false);
                });

                // Прерываем выполнение метода. Плеер ждет твоих действий (Play, Next или Prev).
                return;
            }

            // Включаем видимость видеоконтейнера, если это MP4 из кэша или прямой стрим с YouTube
            bool isVideoActive = mediaUri.EndsWith(".mp4") || item is YouTubeTrack;

            // 3. Запуск воспроизведения
            _mediaPlayer.Media = new Media(_libVLC, new Uri(mediaUri));
            _mediaPlayer.Volume = (_pendingSeekTime > 0) ? 0 : (int)sldVolume.Value;

            Dispatcher.Invoke(() => {
                txtCurrentTrack.Text = item.Title;
                if (!string.IsNullOrEmpty(item.OrderedBy))
                {
                    txtOrderedBy.Text = $"Заказал: {item.OrderedBy}";
                    txtOrderedBy.Visibility = Visibility.Visible;
                }
                else
                {
                    // Вместо Collapsed используем Hidden
                    txtOrderedBy.Visibility = Visibility.Hidden;
                }

                if (_pendingSeekTime <= 0) sldProgress.Value = 0;

                MainVideoView.Opacity = isVideoActive ? 1 : 0;
                VideoContainer.Background = isVideoActive ? Brushes.Black : Brushes.Transparent;

                SyncPlaylistSelection();
                StartMarqueeAnimation();
            });

            _mediaPlayer.Play();
        }

        private void StartMarqueeAnimation()
        {
            // Очистка старой анимации
            txtCurrentTrack.Foreground = Brushes.White;
            textTransform.BeginAnimation(TranslateTransform.XProperty, null);
            textTransform.X = 0;

            // Возвращаем центр для замера (важно для коротких треков)
            txtCurrentTrack.HorizontalAlignment = HorizontalAlignment.Center;

            // Ждем обновления макета для точных расчетов
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Ссылаемся на borderTitle вместо canvTitle
                double containerWidth = borderTitle.ActualWidth;
                double textWidth = txtCurrentTrack.ActualWidth;

                if (textWidth > containerWidth)
                {
                    // Текст длинный: переключаем на левый край, чтобы было откуда крутить
                    txtCurrentTrack.HorizontalAlignment = HorizontalAlignment.Left;

                    double diff = textWidth - containerWidth;
                    // Скорость прокрутки: чем длиннее текст, тем дольше анимация
                    double duration = diff * 0.05 + 2;

                    DoubleAnimation marqueeAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = -(diff + 30), // Запас 30 пикселей, чтобы увидеть конец названия
                        Duration = TimeSpan.FromSeconds(duration),
                        RepeatBehavior = RepeatBehavior.Forever,
                        AutoReverse = true,
                        BeginTime = TimeSpan.FromSeconds(1.5) // Пауза в 1.5 сек перед стартом
                    };

                    // Добавляем плавность в начале и конце
                    marqueeAnim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

                    textTransform.BeginAnimation(TranslateTransform.XProperty, marqueeAnim);
                }
                else
                {
                    // Текст короткий: просто центрируем
                    txtCurrentTrack.HorizontalAlignment = HorizontalAlignment.Center;
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void PlayNext()
        {
            if (_playlistIndex < _sessionPlaylist.Count - 1)
            {
                _playlistIndex++;
                PlayTrack(_sessionPlaylist[_playlistIndex]);
            }

            // При каждом переходе вперед докидываем 1 новый трек в конец буфера
            EnsureBufferIsFilled();
        }

        private void PlayPrev()
        {
            if (_mediaPlayer.Time > 5000)
            {
                _mediaPlayer.Time = 0;
                Dispatcher.Invoke(() => sldProgress.Value = 0);
            }
            else
            {
                if (_playlistIndex > 0)
                {
                    _playlistIndex--;
                    PlayTrack(_sessionPlaylist[_playlistIndex]);
                    UpdateNextTrackPreview(); // Обновляем "Далее" при шаге назад
                }
                else
                {
                    _mediaPlayer.Time = 0;
                    Dispatcher.Invoke(() => sldProgress.Value = 0);
                }
            }
        }
        private void UpdateTransparency(bool hasVideo)
        {
            Dispatcher.Invoke(() => {
                MainVideoView.Opacity = hasVideo ? 1 : 0;
                VideoContainer.Background = hasVideo ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.Transparent;
            });
        }

        #region UI Events

        private void TogglePlay()
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                UpdateTaskbarIcon(false);
                UpdatePlayPauseUI(false);
            }
            else
            {
                // РАСЧЕТ РИСКОВ: Если медиа пустое (из-за ошибки yt-dlp), 
                // кнопка Play работает как "Повторить попытку" для текущего трека.
                if (_mediaPlayer.Media == null && _playlistIndex >= 0 && _playlistIndex < _sessionPlaylist.Count)
                {
                    PlayTrack(_sessionPlaylist[_playlistIndex]);
                    return;
                }

                _mediaPlayer.Play();
                UpdateTaskbarIcon(true);
                UpdatePlayPauseUI(true);
            }
        }
        // --- Обработчики для кнопок в ИНТЕРФЕЙСЕ (RoutedEventArgs) ---
        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayNext();
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => PlayPrev();

        // --- Обработчики для кнопок в ТАСКБАРЕ (EventArgs) ---
        private void BtnPlayPause_Click(object sender, EventArgs e) => TogglePlay();
        private void BtnNext_Click(object sender, EventArgs e) => PlayNext();
        private void BtnPrev_Click(object sender, EventArgs e) => PlayPrev();

        private void UpdateTaskbarIcon(bool isPlaying)
        {
            if (taskbarPlayGeometry == null) return;

            if (isPlaying)
            {
                // Показываем кнопку "Пауза" (две полоски), так как музыка ИГРАЕТ
                taskbarPlayGeometry.Geometry = Geometry.Parse("M0,0 H3 V10 H0 Z M5,0 H8 V10 H5 Z");
                taskbarPlayPause.Description = "Pause";
                btnPlayPause.Content = "⏸";
            }
            else
            {
                // Показываем кнопку "Play" (треугольник), так как музыка СТОИТ
                taskbarPlayGeometry.Geometry = Geometry.Parse("M0,0 L10,5 L0,10 Z");
                taskbarPlayPause.Description = "Play";
                btnPlayPause.Content = "▶";
            }
        }
        private void sldProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Здесь больше НЕТ команды _mediaPlayer.Time = ...
            // Это событие теперь отвечает ТОЛЬКО за перемещение ползунка в интерфейсе
        }
        private void sldProgress_MouseDown(object sender, MouseButtonEventArgs e) => _isUserSeeking = true;
        private void sldProgress_MouseUp(object sender, MouseButtonEventArgs e) => _isUserSeeking = false;
        private void sldProgress_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var slider = sender as Slider;
            if (slider == null) return;

            _isUserSeeking = true;
            slider.CaptureMouse();

            // 1. Мгновенный прыжок при клике
            UpdateSliderValueFromMouse(slider, e.GetPosition(slider).X);
        }
        private void sldProgress_MouseMove(object sender, MouseEventArgs e)
        {
            // 2. Плавное движение ползунка вслед за мышкой
            if (_isUserSeeking && e.LeftButton == MouseButtonState.Pressed)
            {
                var slider = sender as Slider;
                UpdateSliderValueFromMouse(slider, e.GetPosition(slider).X);
            }
        }
        private void sldProgress_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isUserSeeking)
            {
                var slider = sender as Slider;
                if (slider != null)
                {
                    slider.ReleaseMouseCapture();
                    // 3. Отправляем финальную позицию в плеер ТОЛЬКО при отпускании (или клике)
                    _mediaPlayer.Position = (float)slider.Value;
                }
                _isUserSeeking = false;
            }
        }
        private void UpdateSliderValueFromMouse(Slider slider, double mouseX)
        {
            // Костыль: учитываем ширину Thumb из XAML (14px).
            // Половина ширины (7px) — это то самое смещение, которое мы компенсируем.
            double thumbWidth = 14.0;
            double halfThumb = thumbWidth / 2.0;

            // Рассчитываем пропорцию так, чтобы 0 был на 7-м пикселе, а 1 — на (Width - 7).
            double ratio = (mouseX - halfThumb) / (slider.ActualWidth - thumbWidth);

            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            slider.Value = ratio;
        }
        private void Timer_Tick(object? sender, EventArgs e)
        {
            // Если пользователь сейчас не тянет ползунок — обновляем его из плеера
            if (_mediaPlayer.IsPlaying && !_isUserSeeking)
            {
                // Устанавливаем максимум 1.0 для соответствия свойству Position
                sldProgress.Maximum = 1.0;
                sldProgress.Value = _mediaPlayer.Position;
            }
        }
        private void Window_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Close();
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        #endregion

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<PlayerConfig>(json) ?? new();

                    _sessionPlaylist.Clear();
                    foreach (var item in _config.SavedPlaylist)
                    {
                        // Для локальных файлов проверяем, не удалил ли их пользователь
                        if (item is LocalTrack && !File.Exists(item.Id)) continue;
                        _sessionPlaylist.Add(item);
                    }

                    _playlistIndex = _config.PlaylistIndex;
                    sldVolume.Value = _config.Volume;
                    _mediaPlayer.Volume = (int)_config.Volume;

                    if (_config.LastPositionMs > 0)
                    {
                        _pendingSeekTime = _config.LastPositionMs;
                    }
                }
            }
            catch { /* Игнорируем ошибки десериализации для автономности */ }
        }

        private void SaveSettings()
        {
            _config.Volume = sldVolume.Value;
            _config.LastPositionMs = _mediaPlayer.Time;
            _config.PlaylistIndex = _playlistIndex;

            // Сохраняем весь список (включая будущие треки)
            _config.SavedPlaylist = _sessionPlaylist.ToList();

            // Лимит: 200 треков (примерно 100 до и 100 после)
            if (_config.SavedPlaylist.Count > 200)
            {
                int removeCount = _config.SavedPlaylist.Count - 200;
                _config.SavedPlaylist.RemoveRange(0, removeCount);
                _config.PlaylistIndex -= removeCount;
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, options));
        }

        private void BtnOpenPlaylist_Click(object sender, RoutedEventArgs e)
        {
            // Если окно уже создано и отображается — закрываем его
            if (_playlistWindow != null && _playlistWindow.IsLoaded)
            {
                _playlistWindow.Close();
                return;
            }

            // В противном случае создаем новое окно
            _playlistWindow = new PlaylistWindow(_sessionPlaylist, _musicFiles, _playlistIndex, (item, playNow) =>
            {
                if (playNow)
                {
                    if (!_sessionPlaylist.Contains(item))
                    {
                        _sessionPlaylist.Insert(_playlistIndex + 1, item);
                        _playlistIndex++;
                    }
                    else
                    {
                        _playlistIndex = _sessionPlaylist.IndexOf(item);
                    }
                    PlayTrack(_sessionPlaylist[_playlistIndex]);
                }
                else
                {
                    _sessionPlaylist.Insert(_playlistIndex + 1, item);
                    UpdateNextTrackPreview();

                    var originalTitle = txtCurrentTrack.Text;
                    txtCurrentTrack.Text = "✓ Добавлено в очередь";
                    Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => txtCurrentTrack.Text = originalTitle));
                }

                SaveSettings();
            });

            // Эта строка критически важна: она позволяет кнопке снова открыть окно после закрытия
            _playlistWindow.Closed += (s, args) => _playlistWindow = null;

            _playlistWindow.Owner = this;
            _playlistWindow.Show();
        }

        private void SyncPlaylistSelection()
        {
            if (_playlistWindow != null && _playlistWindow.IsLoaded)
            {
                _playlistWindow.UpdateSelection(_playlistIndex);
            }
        }

        private void sldVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Расчет шага: 2% за один щелчок колеса. 
            // e.Delta > 0 означает прокрутку вверх.
            double step = 2;
            if (e.Delta > 0)
                sldVolume.Value = Math.Min(sldVolume.Maximum, sldVolume.Value + step);
            else
                sldVolume.Value = Math.Max(sldVolume.Minimum, sldVolume.Value - step);

            // Помечаем событие как обработанное, чтобы не скроллился весь интерфейс
            e.Handled = true;
        }

        private void sldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int vol = (int)e.NewValue;

            // Обновляем плеер
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = vol;
            }

            // Мгновенное обновление ToolTip
            if (volToolTip != null)
            {
                volToolTip.Content = $"{vol}%";

                // Маленький хак: если ToolTip уже открыт, принудительно перерисовываем его
                if (volToolTip.IsOpen)
                {
                    // Это заставляет WPF обновить положение или содержимое подсказки
                    volToolTip.HorizontalOffset += 0.001;
                    volToolTip.HorizontalOffset -= 0.001;
                }
            }
        }

        private async void BtnAttachVideo_Click(object sender, RoutedEventArgs e)
        {
            if (!Clipboard.ContainsText()) return;

            string url = Clipboard.GetText();
            if (!url.Contains("youtube.com") && !url.Contains("youtu.be")) return;

            if (_playlistIndex >= 0 && _sessionPlaylist[_playlistIndex] is LocalTrack current)
            {
                string originalTitle = current.Title;
                txtCurrentTrack.Text = "⏳ Загрузка видеоряда...";

                if (!Directory.Exists(_cacheFolder)) Directory.CreateDirectory(_cacheFolder);
                string outputPath = Path.Combine(_cacheFolder, originalTitle + ".mp4");

                try
                {
                    await YtDlpHelper.DownloadVideoAsync(url, outputPath);

                    // Если мы всё еще на этом же треке — ПЕРЕЗАПУСКАЕМ С НУЛЯ
                    if (_playlistIndex < _sessionPlaylist.Count && _sessionPlaylist[_playlistIndex].Id == current.Id)
                    {
                        // Принудительно сбрасываем ожидание позиции, чтобы начать с 0:00
                        _pendingSeekTime = -1;
                        PlayTrack(current);
                    }
                }
                catch (Exception ex)
                {
                    txtCurrentTrack.Text = "❌ Ошибка скачивания";
                    await Task.Delay(2000);
                    txtCurrentTrack.Text = originalTitle;
                    System.Diagnostics.Debug.WriteLine($"yt-dlp error: {ex.Message}");
                }
            }
        }
        private async void txtCurrentTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(txtCurrentTrack.Text) || txtCurrentTrack.Text == "Загрузка...") return;
            if (_playlistIndex < 0 || _playlistIndex >= _sessionPlaylist.Count) return;

            try
            {
                var currentItem = _sessionPlaylist[_playlistIndex];

                // Логика выбора контента: 
                // Если перед нами YouTubeTrack — генерируем ссылку по Id.
                // Если LocalTrack — берем заголовок трека (Title).
                string contentToCopy = currentItem is YouTubeTrack yt
                    ? $"https://www.youtube.com/watch?v={yt.Id}"
                    : currentItem.Title;

                Clipboard.SetText(contentToCopy);

                // Визуальное подтверждение
                string originalTitle = txtCurrentTrack.Text;
                textTransform.BeginAnimation(TranslateTransform.XProperty, null);

                txtCurrentTrack.Text = "📋 Скопировано!";
                txtCurrentTrack.Foreground = Brushes.LightGreen;

                await Task.Delay(1000);

                txtCurrentTrack.Text = originalTitle;
                txtCurrentTrack.Foreground = Brushes.White;

                // Перезапуск анимации прокрутки
                StartMarqueeAnimation();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка копирования: {ex.Message}");
            }
        }
        private async Task<(string id, string title)?> GetYouTubeVideoInfoAsync(string url)
        {
            if (!YtDlpHelper.IsValidYouTubeUrl(url))
            {
                System.Diagnostics.Debug.WriteLine($"[API] Отклонено: Некорректный URL - {url}");
                return null;
            }

            // Явно указываем возвращаемый тип для Task.Run, чтобы разрешить возврат null
            return await Task.Run<(string id, string title)?>(() =>
            {
                try
                {
                    using (var process = new System.Diagnostics.Process())
                    {
                        process.StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "yt-dlp",
                            Arguments = $"--get-title --get-id --no-playlist \"{url}\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        process.Start();

                        // Читаем все строки (обычно их две: заголовок и ID)
                        string title = process.StandardOutput.ReadLine()?.Trim();
                        string id = process.StandardOutput.ReadLine()?.Trim();
                        process.WaitForExit();

                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
                        {
                            return (id, title);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Ошибка yt-dlp: {ex.Message}");
                }
                return null; // Теперь null допустим, так как тип кортежа - (string, string)?
            });
        }

        private async void ProcessTrackOrder(string url, string username)
        {
            string originalTitle = txtCurrentTrack.Text;
            txtCurrentTrack.Text = $"⏳ Заказ от {username}...";

            var info = await GetYouTubeVideoInfoAsync(url);
            if (info.HasValue)
            {
                var newTrack = new YouTubeTrack(info.Value.id, info.Value.title) { OrderedBy = username };

                // Вставляем после всех существующих YouTube-заказов
                int insIndex = _playlistIndex + 1;
                while (insIndex < _sessionPlaylist.Count && _sessionPlaylist[insIndex] is YouTubeTrack)
                    insIndex++;

                _sessionPlaylist.Insert(insIndex, newTrack);

                UpdateNextTrackPreview();
                SyncPlaylistSelection();
                SaveSettings();

                txtCurrentTrack.Text = $"✓ Добавлено ({username})";
            }
            else
            {
                txtCurrentTrack.Text = "❌ Ошибка заказа";
            }

            await Task.Delay(1500);
            txtCurrentTrack.Text = originalTitle;
        }

        // ОБНОВЛЕНИЕ КНОПКИ В UI
        private void BtnAddYouTubeTrack_Click(object sender, RoutedEventArgs e)
        {
            if (Clipboard.ContainsText())
            {
                ProcessTrackOrder(Clipboard.GetText(), "admin");
            }
        }
        private async void StartApiServer()
        {
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add("http://localhost:3009/");
            try
            {
                listener.Start();
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
            }
            catch { /* Логирование ошибок порта */ }
        }

        private async Task HandleRequest(System.Net.HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "POST")
            {
                try
                {
                    using (var reader = new StreamReader(context.Request.InputStream))
                    {
                        var body = await reader.ReadToEndAsync();
                        var data = JsonSerializer.Deserialize<ApiRequest>(body);
                        if (data != null && !string.IsNullOrEmpty(data.url))
                        {
                            // Вызываем общую функцию обработки заказа
                            Dispatcher.Invoke(() => ProcessTrackOrder(data.url, data.user ?? "Аноним"));
                        }
                    }
                }
                catch { }
            }
            context.Response.StatusCode = 200;
            context.Response.Close();
        }

        // Вспомогательный класс для десериализации
        public class ApiRequest { public string url { get; set; } public string user { get; set; } }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(handle);
            source.AddHook(HwndHook);

            // Регистрируем клавиши (0 — без модификаторов типа Ctrl/Alt)
            RegisterHotKey(handle, HOTKEY_ID + 1, 0, VK_MEDIA_NEXTTRACK);
            RegisterHotKey(handle, HOTKEY_ID + 2, 0, VK_MEDIA_PREVTRACK);
            RegisterHotKey(handle, HOTKEY_ID + 3, 0, VK_MEDIA_PLAY_PAUSE);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;

            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                switch (id)
                {
                    case HOTKEY_ID + 1: // NEXT
                        BtnNext_Click(null, null);
                        handled = true;
                        break;
                    case HOTKEY_ID + 2: // PREV
                        BtnPrev_Click(null, null);
                        handled = true;
                        break;
                    case HOTKEY_ID + 3: // PLAY/PAUSE
                        BtnPlayPause_Click(null, null);
                        handled = true;
                        break;
                }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID + 1);
            UnregisterHotKey(handle, HOTKEY_ID + 2);
            UnregisterHotKey(handle, HOTKEY_ID + 3);
            base.OnClosed(e);
        }
    }

    public class PlayerConfig
    {
        public double Volume { get; set; } = 50;
        public long LastPositionMs { get; set; } = 0;
        public int PlaylistIndex { get; set; } = -1; // Где мы остановились
        public List<PlaylistItem> SavedPlaylist { get; set; } = new(); // История + Будущее
    }
}