// PatchController.cs — HttpClient, progreso y velocidad, y sin cierres inesperados.
// Compatible con .NET Framework/WPF clásico.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WoWLauncher.Patcher
{
    internal class PatchController
    {
        private readonly MainWindow m_WndRef;

        private string m_PatchListUri = "";
        private string m_PatchUri = "";

        private readonly Dictionary<string, string> m_PatchList;
        private readonly List<string> m_DownloadQueue;
        private int m_CurrentIndex;

        private readonly HttpClient _http;
        private CancellationTokenSource _cts;
        private readonly Stopwatch _speedSw = new Stopwatch();

        // Acumuladores de progreso
        private long _bytesTotalExpected;
        private long _bytesTotalDownloaded;

        public bool IsPatching { get; private set; }

        public PatchController(MainWindow wndRef)
        {
            m_WndRef = wndRef;
            m_PatchList = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            m_DownloadQueue = new List<string>();
            IsPatching = false;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                UseProxy = true
            };
            _http = new HttpClient(handler);
            _http.Timeout = TimeSpan.FromMinutes(10);

            _cts = new CancellationTokenSource();
        }

        public async void CheckPatch(bool _init = true)
        {
            try
            {
                // === Resolver URLs base ===
                string verFile = Path.Combine(AppContext.BaseDirectory, "Cache", "L", "version.txt");
                string ver = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : "0.0";

                string tagV = "v" + ver;
                string baseUrl = $"https://github.com/ninjapal453/Actualizador-Wow/releases/download/{tagV}/";
                string testUrl = baseUrl + "plist.txt";

                if (!await UrlExistsAsync(testUrl))
                {
                    string tagNoV = ver;
                    baseUrl = $"https://github.com/ninjapal453/Actualizador-Wow/releases/download/{tagNoV}/";
                    testUrl = baseUrl + "plist.txt";
                }

                if (!await UrlExistsAsync(testUrl))
                {
                    baseUrl = "https://raw.githubusercontent.com/ninjapal453/Actualizador-Wow/main/Patch/";
                    testUrl = baseUrl + "plist.txt";
                }

                m_PatchUri = baseUrl;
                m_PatchListUri = testUrl;

                // === Descargar y procesar plist ===
                string rawData = await _http.GetStringAsync(m_PatchListUri);
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Cache", "P"));
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "Cache", "P", "plist_debug.txt"), rawData);

                m_PatchList.Clear();
                var lines = rawData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string file = parts[0].Trim();
                    string hash = parts[1].Trim().ToLowerInvariant();
                    m_PatchList[file] = hash;
                }

                // === Preparar cola de descargas ===
                m_DownloadQueue.Clear();
                _bytesTotalExpected = 0;
                _bytesTotalDownloaded = 0;

                foreach (var kvp in m_PatchList)
                {
                    string localPath = Path.Combine(AppContext.BaseDirectory, "Data", kvp.Key); // Unificado "Data"
                    if (!File.Exists(localPath) || GetMD5(localPath) != kvp.Value)
                    {
                        m_DownloadQueue.Add(kvp.Key);

                        // Intentar leer tamaño por adelantado (para barra global)
                        long size = await TryGetContentLengthAsync(m_PatchUri + kvp.Key);
                        if (size > 0) _bytesTotalExpected += size;
                    }
                }

                if (m_DownloadQueue.Count == 0)
                {
                    IsPatching = false;
                    await UI(() =>
                    {
                        m_WndRef.progressInfo.Visibility = System.Windows.Visibility.Visible;
                        m_WndRef.progressBar.Value = 1;
                        m_WndRef.progressBar.Maximum = 1;
                        m_WndRef.progressInfo.Text = "Parches actualizados.";
                        m_WndRef.playBtn.IsEnabled = true;
                    });
                    return;
                }

                // === Descargar en serie con progreso ===
                IsPatching = true;
                _speedSw.Restart();

                await UI(() =>
                {
                    m_WndRef.progressInfo.Visibility = System.Windows.Visibility.Visible;
                    m_WndRef.playBtn.IsEnabled = false;
                    m_WndRef.progressBar.Value = 0;
                    m_WndRef.progressBar.Maximum = Math.Max((double)_bytesTotalExpected, 1.0);
                });

                for (m_CurrentIndex = 0; m_CurrentIndex < m_DownloadQueue.Count; m_CurrentIndex++)
                {
                    string file = m_DownloadQueue[m_CurrentIndex];
                    string url = m_PatchUri + file;
                    string targetPath = Path.Combine(AppContext.BaseDirectory, "Data", file);

                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    await DownloadFileAsync(url, targetPath, (bytesThisFile, elapsed) =>
                    {
                        long downloadedSoFarLong = _bytesTotalDownloaded + bytesThisFile;

                        UI_NoAwait(() =>
                        {
                            m_WndRef.progressBar.Value = Math.Min((double)downloadedSoFarLong, m_WndRef.progressBar.Maximum);

                            double percent =
                                m_WndRef.progressBar.Maximum > 0
                                ? (m_WndRef.progressBar.Value / m_WndRef.progressBar.Maximum) * 100.0
                                : 0.0;

                            string speedStr = FormatSpeed(bytesThisFile, elapsed);

                            m_WndRef.progressInfo.Text =
                                string.Format(
                                    "{0:0.0}%  •  {1}  ({2}/{3})  Descargado {4:0.0}/{5:0.0} MB  Velocidad {6}",
                                    percent,
                                    file,
                                    m_CurrentIndex + 1,
                                    m_DownloadQueue.Count,
                                    ToMB(downloadedSoFarLong),
                                    ToMB(Math.Max(_bytesTotalExpected, downloadedSoFarLong)),
                                    speedStr
                                );
                        });
                    });

                    _bytesTotalDownloaded += new FileInfo(targetPath).Length;
                }

                _speedSw.Stop();
                IsPatching = false;

                await UI(() =>
                {
                    m_WndRef.progressBar.Value = m_WndRef.progressBar.Maximum;
                    m_WndRef.progressInfo.Text = "Descarga de parches completada.";
                    m_WndRef.playBtn.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                IsPatching = false;
                try
                {
                    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Cache", "P"));
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "Cache", "P", "patch_log.txt"),
                        "[" + DateTime.Now + "] ERROR: " + ex + Environment.NewLine);
                }
                catch { }

                await UI(() =>
                {
                    System.Windows.MessageBox.Show(m_WndRef,
                        "Error en la descarga de parches:\n" + ex.Message +
                        "\n\nConsulta Cache/P/patch_log.txt y Cache/P/plist_debug.txt",
                        "Patch Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    m_WndRef.progressInfo.Text = "Error en la descarga de parches.";
                    m_WndRef.playBtn.IsEnabled = true;
                });
            }
        }

        // --------- Helpers ---------

        private async Task<bool> UrlExistsAsync(string url)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Head, url);
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token))
                {
                    return resp.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        private async Task<long> TryGetContentLengthAsync(string url)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Head, url);
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token))
                {
                    if (resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentLength.HasValue)
                        return resp.Content.Headers.ContentLength.Value;
                }
            }
            catch { }
            return 0;
        }

        private async Task DownloadFileAsync(string url, string targetPath, Action<long, TimeSpan> onProgress)
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token))
            {
                resp.EnsureSuccessStatusCode();

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long bytesThisFile = 0;
                    var sw = Stopwatch.StartNew();

                    while (true)
                    {
                        int read = await src.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                        if (read <= 0) break;

                        await dst.WriteAsync(buffer, 0, read, _cts.Token);
                        bytesThisFile += read;

                        if (sw.ElapsedMilliseconds >= 200)
                        {
                            onProgress(bytesThisFile, sw.Elapsed);
                            sw.Restart();
                        }
                    }

                    onProgress(bytesThisFile, TimeSpan.FromMilliseconds(250));
                }
            }
        }

        private static string FormatSpeed(long bytesRead, TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds <= 0.0001) return "0 MB/s";
            double speed = bytesRead / elapsed.TotalSeconds / (1024.0 * 1024.0);
            return string.Format("{0:0.00} MB/s", speed);
        }

        private static double ToMB(long bytes)
        {
            return bytes / (1024.0 * 1024.0);
        }

        private static string GetMD5(string filePath)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private Task UI(Action a) => m_WndRef.Dispatcher.InvokeAsync(a).Task;
        private void UI_NoAwait(Action a) => m_WndRef.Dispatcher.Invoke(a);
    }
}
