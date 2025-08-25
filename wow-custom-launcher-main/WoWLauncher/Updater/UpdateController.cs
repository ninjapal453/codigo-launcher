// UpdateController.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;

namespace WoWLauncher.Updater
{
    internal class UpdateController
    {
        private readonly MainWindow m_WndRef;

        // Host/IP del realmlist actual
        private string m_RealmAddress;

        // WebClients persistentes para evitar Dispose prematuro con Async
        private WebClient _wcUpdate;
        private WebClient _wcRealm;

        // URLs en GitHub
        private const string UpdateVersionUri = "https://92e01281701b8cee8449177420951cf8.r2.cloudflarestorage.com/patcher/launcher/update.txt";
        private const string ServerAddressUri = "https://92e01281701b8cee8449177420951cf8.r2.cloudflarestorage.com/patcher/realm.txt";
        // Rutas locales
        private static readonly string CacheDirL = Path.Combine(AppContext.BaseDirectory, "Cache", "L");
        private static readonly string DataDirEsES = Path.Combine(AppContext.BaseDirectory, "Data", "esES"); // <- Data en mayúscula
        private static readonly string VersionFile = Path.Combine(CacheDirL, "version.txt");
        private static readonly string InProgress = Path.Combine(CacheDirL, "update_in_progress.flag");
        private static readonly string RealmlistWtf = Path.Combine(DataDirEsES, "realmlist.wtf");

        public string RealmAddress => m_RealmAddress;

        // === Helper público para ServerCheck ===
        public string GetRealmAddressSafe()
        {
            // Devuelve localhost si aún no hay host válido
            return string.IsNullOrWhiteSpace(m_RealmAddress) ? "0.0.0.0" : m_RealmAddress;
        }

        public UpdateController(MainWindow wndRef)
        {
            m_WndRef = wndRef;
            Directory.CreateDirectory(CacheDirL);
            Directory.CreateDirectory(DataDirEsES);
        }

        // ==== Comprobar updates del launcher ====
        public void CheckForUpdates()
        {
            try
            {
                // Reciclar cliente previo si lo hubiera
                if (_wcUpdate != null)
                {
                    _wcUpdate.DownloadStringCompleted -= update_DoneRetrieveAsync;
                    _wcUpdate.Dispose();
                    _wcUpdate = null;
                }

                _wcUpdate = new WebClient();
                _wcUpdate.DownloadStringCompleted += update_DoneRetrieveAsync;
                string url = UpdateVersionUri + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _wcUpdate.DownloadStringAsync(new Uri(url));
            }
            catch
            {
                // Ignorar
            }
        }

        private void update_DoneRetrieveAsync(object sender, DownloadStringCompletedEventArgs e)
        {
            try
            {
                string onlineRaw = ((e.Result ?? "0.0").Trim());
                string[] parts = onlineRaw.Split('.');
                string onlineMajorMinor = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}"
                                                            : (parts.Length == 1 ? $"{parts[0]}.0" : "0.0");

                string localRaw = (Assembly.GetExecutingAssembly().GetName().Version != null
                    ? Assembly.GetExecutingAssembly().GetName().Version.ToString()
                    : "0.0").Trim();
                string[] lparts = localRaw.Split('.');
                string localMajorMinor = lparts.Length >= 2 ? $"{lparts[0]}.{lparts[1]}"
                                                            : (lparts.Length == 1 ? $"{lparts[0]}.0" : "0.0");

                Version onlineVer = Version.TryParse(onlineMajorMinor, out var v1) ? v1 : new Version(0, 0);
                Version localVer = Version.TryParse(localMajorMinor, out var v2) ? v2 : new Version(0, 0);

                Directory.CreateDirectory(CacheDirL);
                File.WriteAllText(VersionFile, onlineMajorMinor);

                if (onlineVer > localVer)
                {
                    if (File.Exists(InProgress))
                    {
                        File.Delete(InProgress);
                        MessageBox.Show(m_WndRef,
                            "Se detectó una actualización, pero el ejecutable no fue reemplazado.\n" +
                            "Verifica que Updater descargue y extraiga el client.zip con permisos correctos.",
                            "Actualización no aplicada",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var result = MessageBox.Show(m_WndRef,
                        "Hay una actualización del launcher. ¿Quieres actualizar ahora?",
                        "Update disponible!", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        string updaterExe = Path.Combine(AppContext.BaseDirectory, "Updater.exe");
                        if (File.Exists(updaterExe))
                        {
                            File.WriteAllText(InProgress, DateTime.UtcNow.ToString("o"));
                            var pid = Process.GetCurrentProcess().Id;

                            // Lanza el Updater primero (con el PID actual) y luego cierra
                            Process.Start(new ProcessStartInfo(updaterExe)
                            {
                                UseShellExecute = true,
                                Arguments = pid.ToString()
                            });

                            Application.Current.Shutdown();
                        }
                    }
                }
                else
                {
                    if (File.Exists(InProgress)) File.Delete(InProgress);
                }
            }
            catch
            {
                // Ignorar errores silenciosamente
            }
            finally
            {
                if (_wcUpdate != null)
                {
                    _wcUpdate.DownloadStringCompleted -= update_DoneRetrieveAsync;
                    _wcUpdate.Dispose();
                    _wcUpdate = null;
                }
            }
        }

        // ==== Actualizar Realmlist ====
        public void RetrieveRealmIP()
        {
            // Valor por defecto (por si falla la descarga)
            m_RealmAddress = "wow.horizongames.es";

            try
            {
                if (_wcRealm != null)
                {
                    _wcRealm.DownloadStringCompleted -= realm_DonePatchListAsync;
                    _wcRealm.Dispose();
                    _wcRealm = null;
                }

                _wcRealm = new WebClient();
                _wcRealm.DownloadStringCompleted += realm_DonePatchListAsync;
                string url = ServerAddressUri + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _wcRealm.DownloadStringAsync(new Uri(url));
            }
            catch
            {
                FallbackRealmFromLocal();
            }
        }

        private void realm_DonePatchListAsync(object sender, DownloadStringCompletedEventArgs e)
        {
            try
            {
                string addr = (e.Result ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(addr))
                    m_RealmAddress = addr;

                Directory.CreateDirectory(DataDirEsES);
                File.WriteAllText(RealmlistWtf, $"set realmlist {m_RealmAddress}");
            }
            catch
            {
                FallbackRealmFromLocal();
            }
            finally
            {
                if (_wcRealm != null)
                {
                    _wcRealm.DownloadStringCompleted -= realm_DonePatchListAsync;
                    _wcRealm.Dispose();
                    _wcRealm = null;
                }
            }
        }

        private void FallbackRealmFromLocal()
        {
            try
            {
                Directory.CreateDirectory(DataDirEsES);
                if (File.Exists(RealmlistWtf))
                {
                    string content = File.ReadAllText(RealmlistWtf).Trim();
                    var parts = content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && parts[0] == "set" && parts[1] == "realmlist")
                        m_RealmAddress = parts[2];
                }
                else
                {
                    File.WriteAllText(RealmlistWtf, $"set realmlist {m_RealmAddress}");
                }
            }
            catch
            {
                // Ignorar
            }
        }
    }
}
