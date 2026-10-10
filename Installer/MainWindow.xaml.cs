using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace TGYH_TR_Installer
{
    public partial class MainWindow : Window
    {
        private string _gameDir;

        public MainWindow()
        {
            InitializeComponent();

            string found = GameLocator.Find();
            if (found != null) SetGameFolder(found);
        }

        private void SelectGameFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Oyunun .exe Dosyası|" + PatchInstaller.GameExeName,
                Title = "Oyun Klasörünü Seçin"
            };
            if (dialog.ShowDialog() == true)
                SetGameFolder(Path.GetDirectoryName(dialog.FileName));
        }

        private void SetGameFolder(string dir)
        {
            _gameDir = dir;
            FolderText.Text = dir;
            RefreshState();
        }

        private void RefreshState()
        {
            if (_gameDir == null) return;
            try
            {
                var state = new PatchInstaller(_gameDir).Inspect();

                string version = state.GameVersion != null ? "Oyun sürümü: " + state.GameVersion : "Oyun sürümü algılanamadı";
                string patch;
                if (state.PatchInstalled)
                    patch = "Yama yüklü" + (state.PatchVersion != null ? " (v" + state.PatchVersion + ")" : "");
                else if (state.LegacyPatchInstalled)
                    patch = "Eski yama yüklü; yüklemede yenisiyle değiştirilecek";
                else
                    patch = "Yama yüklü değil";
                InfoText.Text = version + "  •  " + patch;

                InstallButton.Content = state.PatchInstalled ? "Yamayı Güncelle" : "Yamayı Yükle";
                InstallButton.IsEnabled = true;
                UninstallButton.IsEnabled = state.PatchInstalled || state.LegacyPatchInstalled;
                StatusText.Text = "Durum: İşlem bekleniyor...";
            }
            catch (Exception ex)
            {
                InfoText.Text = "";
                InstallButton.IsEnabled = false;
                UninstallButton.IsEnabled = false;
                StatusText.Text = "Durum: Oyun klasörü okunamadı: " + ex.Message;
            }
        }

        private PatchInstaller CreateInstaller()
        {
            return new PatchInstaller(_gameDir)
            {
                Status = text => Dispatcher.InvokeAsync(() => StatusText.Text = "Durum: " + text),
                Progress = percent => Dispatcher.InvokeAsync(() => ProgressBar.Value = percent),
                LocalPackage = App.FindLocalPackage(),
            };
        }

        private bool GameIsRunning()
        {
            string name = Path.GetFileNameWithoutExtension(PatchInstaller.GameExeName);
            if (!Process.GetProcessesByName(name).Any()) return false;
            MessageBox.Show("Oyun açık. Lütfen önce oyunu kapatın.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (GameIsRunning()) return;
            SetBusy(true);
            try
            {
                var installer = CreateInstaller();
                await Task.Run(() => installer.InstallAsync());
                MessageBox.Show(
                    "Yama başarıyla yüklendi!\n\nOyunun ilk açılışı 1-2 dakika sürebilir. " +
                    "Türkçe, Ayarlar > Dil menüsündeki ilk seçenektir.",
                    "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowError("Yükleme hatası", ex);
            }
            SetBusy(false);
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (GameIsRunning()) return;
            SetBusy(true);
            try
            {
                var installer = CreateInstaller();
                await Task.Run(() => installer.Uninstall());
                MessageBox.Show("Yama başarıyla silindi!", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowError("Silme hatası", ex);
            }
            SetBusy(false);
        }

        private void SetBusy(bool busy)
        {
            SelectGameFolderButton.IsEnabled = !busy;
            if (busy)
            {
                InstallButton.IsEnabled = false;
                UninstallButton.IsEnabled = false;
                ProgressBar.Value = 0;
            }
            else
            {
                string status = StatusText.Text;
                RefreshState();
                StatusText.Text = status;
            }
        }

        private void ShowError(string title, Exception ex)
        {
            string message = ex is UnauthorizedAccessException
                ? "Oyun klasörüne yazılamıyor. Kurulum aracını yönetici olarak çalıştırmayı deneyin.\n\n" + ex.Message
                : ex.Message;
            StatusText.Text = "Durum: " + title + ".";
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
