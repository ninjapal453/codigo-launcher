// ServerCheck.cs
using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WoWLauncher.Updater
{
    internal class ServerCheck
    {
        private readonly MainWindow m_WndRef;
        private readonly UpdateController m_UpdaterRef;
        private readonly DispatcherTimer _timer;

        public ServerCheck(MainWindow wndRef, UpdateController updRef)
        {
            m_WndRef = wndRef;
            m_UpdaterRef = updRef;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _timer.Tick += (_, __) => _ = CheckNow();
            _timer.Start();

            m_WndRef.serverStatusIcon.Source = Load("pack://application:,,,/images/Indicator-Yellow.PNG");
            m_WndRef.serverStatus.Text = "Comprobando servidor...";

            _ = CheckNow();
        }

        private static BitmapImage Load(string uri)
        {
            return new BitmapImage(new Uri(uri, UriKind.Absolute));
        }

        private async Task CheckNow()
        {
            string host = "wow.horizongames.es";
            try { host = m_UpdaterRef != null ? m_UpdaterRef.GetRealmAddressSafe() : "wow.horizongames.es"; }
            catch { }

            int port = 8085; // o 8085 si prefieres worldserver
            bool ok = await Probe(host, port, TimeSpan.FromSeconds(2));

            if (ok)
            {
                m_WndRef.serverStatusIcon.Source = Load("pack://application:,,,/images/Indicator-Green.PNG");
                m_WndRef.serverStatus.Text = "Server online!";
            }
            else
            {
                m_WndRef.serverStatusIcon.Source = Load("pack://application:,,,/images/Indicator-Green.PNG");
                m_WndRef.serverStatus.Text = "Server Online.";
                m_WndRef.playBtn.IsEnabled = false;
            }
        }

        private static async Task<bool> Probe(string host, int port, TimeSpan timeout)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(host, port);
                    var delayTask = Task.Delay(timeout);
                    var finished = await Task.WhenAny(connectTask, delayTask);
                    return finished == connectTask && client.Connected;
                }
            }
            catch { return false; }
        }
    }
}
