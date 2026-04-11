using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;

namespace ObsMonolithPlayer
{
    public partial class PlaylistWindow : Window
    {
        private ObservableCollection<PlaylistItem> _session;
        private List<string> _allFiles;
        private Action<PlaylistItem, bool> _onTrackSelected; // bool = playNow

        public PlaylistWindow(ObservableCollection<PlaylistItem> session, List<string> allFiles, int currentIndex, Action<PlaylistItem, bool> onTrackSelected)
        {
            InitializeComponent();
            _session = session;
            _allFiles = allFiles;
            _onTrackSelected = onTrackSelected;

            lstPlaylist.ItemsSource = _session;
            UpdateSelection(currentIndex);

            // Автофокус на поиске
            this.Activated += (s, e) => txtSearch.Focus();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = txtSearch.Text.Trim().ToLower();
            bool isSearching = !string.IsNullOrWhiteSpace(query);

            // Управляем видимостью кнопки "X"
            if (btnClearSearch != null)
                btnClearSearch.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;

            if (!isSearching)
            {
                lstPlaylist.ItemsSource = _session;
                txtPlaceholder.Visibility = Visibility.Visible;

                // КРИТИЧНО: При отмене поиска сбрасываем выделение, 
                // чтобы оно обновилось на актуальное через SyncPlaylistSelection
                lstPlaylist.SelectedIndex = -1;
            }
            else
            {
                txtPlaceholder.Visibility = Visibility.Collapsed;
                var filtered = _allFiles
                    .Where(f => Path.GetFileName(f).ToLower().Contains(query))
                    .Select(f => new LocalTrack(f))
                    .ToList();
                lstPlaylist.ItemsSource = filtered;
            }
        }
        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            txtSearch.Clear();
            txtSearch.Focus();
        }
        private void LstPlaylist_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstPlaylist.SelectedItem is PlaylistItem selected)
            {
                bool isShift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                bool playNow = !isShift;

                _onTrackSelected?.Invoke(selected, playNow);

                if (playNow)
                {
                    this.Close();
                }
                else
                {
                    // СБРОС ВЫДЕЛЕНИЯ после Shift+Click
                    // Это избавляет от визуального обмана, что "этот трек сейчас играет"
                    lstPlaylist.SelectedIndex = -1;
                }
            }
        }
        public void UpdateSelection(int index)
        {
            if (lstPlaylist.ItemsSource == _session && index >= 0 && index < lstPlaylist.Items.Count)
            {
                lstPlaylist.SelectedIndex = index;
                lstPlaylist.ScrollIntoView(lstPlaylist.Items[index]);
            }
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && lstPlaylist.Items.Count > 0)
            {
                // Выбираем первый результат, если нажат Enter
                if (lstPlaylist.SelectedIndex == -1) lstPlaylist.SelectedIndex = 0;
                LstPlaylist_MouseDoubleClick(null, null);
            }
            else if (e.Key == Key.Escape)
            {
                // Двойная логика Escape:
                if (!string.IsNullOrEmpty(txtSearch.Text))
                {
                    txtSearch.Clear(); // Первый Escape чистит поиск
                }
                else
                {
                    this.Close(); // Второй Escape закрывает окно
                }
            }
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Close();
    }
}