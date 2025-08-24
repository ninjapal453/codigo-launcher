// MainWindow.xaml.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using WoWLauncher.Patcher;
using WoWLauncher.Updater;

namespace WoWLauncher
{
    public partial class MainWindow : Window
    {
        private readonly PatchController m_Patcher;
        private readonly UpdateController m_Updater;
        private readonly DispatcherTimer _uiTimer;

        private readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        private readonly string _wowPath = Path.Combine(AppContext.BaseDirectory, "Wow.exe");

        public MainWindow()
        {
            InitializeComponent();

            playBtn.IsEnabled = false;

            try
            {
                if (!Directory.Exists(_dataDir))
                    Directory.CreateDirectory(_dataDir);
            }
            catch (Exception ex)
            {
                progressInfo.Text = "Aviso preparando entorno: " + ex.Message;
            }

            m_Updater = new UpdateController(this);
            m_Patcher = new PatchController(this);

            // Monitor de servidor (sin ref)
            var _ = new ServerCheck(this, m_Updater);

            m_Updater.CheckForUpdates();
            m_Updater.RetrieveRealmIP();

            if (!File.Exists(_wowPath))
                progressInfo.Text = "No se encuentra Wow.exe. Se descargará al aplicar los parches…";

            m_Patcher.CheckPatch();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (_, __) =>
            {
                bool canPlay = !m_Patcher.IsPatching && File.Exists(_wowPath);
                if (playBtn.IsEnabled != canPlay)
                    playBtn.IsEnabled = canPlay;
            };
            _uiTimer.Start();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (!m_Patcher.IsPatching ||
                (m_Patcher.IsPatching && MessageBox.Show(this,
                    "Hay un parche en curso. ¿Seguro que quieres salir?",
                    "Patching", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes))
            {
                Application.Current.Shutdown();
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Uri?.ToString()))
            {
                Process.Start(new ProcessStartInfo(e.Uri.ToString())
                {
                    UseShellExecute = true
                });
            }
        }

        private void playBtn_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(_wowPath) && !m_Patcher.IsPatching)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_wowPath) { UseShellExecute = true });
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "No se pudo iniciar Wow.exe: " + ex.Message,
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else if (m_Patcher.IsPatching)
            {
                MessageBox.Show(this, "Espera a que termine el parche antes de jugar.",
                    "Patching", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, "Aún no se ha descargado Wow.exe.",
                    "Juego no disponible", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
