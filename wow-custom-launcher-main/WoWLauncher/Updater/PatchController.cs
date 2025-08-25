// PatchController.cs — LIVE con R2 público (r2.dev), progreso y velocidad.
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
    public class PatchController
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

        private long _bytesTotalExpected;
        private long _bytesTotalDownloaded;

        public bool IsPatching { get; private set; }

        // === URL PÚBLICA DE TU BUCKET R2 (SIN DOMINIOS PROPIOS) ===
        private const string R2_PUBLIC_BASE = "https://pub-23b561d8083642d6a10b3ecab65d8834.r2.dev/";

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
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            _cts = new CancellationTokenSource();
        }

        // === MÉTODO PÚBLICO ===
        public async void CheckPatch(bool _init = true)
        {
            try
            {
                // 1) Base fija R2 (r2.dev)
                m_PatchUri = EnsureSlash(R2_PUBLIC_BASE); // p.ej. https://...r2.dev/
                m_PatchListUri = m_PatchUri + "plist.txt";

                // 2) Validar existencia de plist.txt (GET range 0-0 para evitar problemas con HEAD)
                if (!await UrlExistsAsync(m_PatchListUri).ConfigureAwait(false))
                    throw new FileNotFoundException("No se encontró plist.txt en la URL pública del bucket R2.\nURL: " + m_PatchListUri);

                // 3) Descargar y parsear plist
                string rawData;
                using (var resp = await _http.GetAsync(m_PatchListUri, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"No se pudo obtener plist.txt: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    rawData = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }

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

                // 4) Preparar cola y bytes totales
                m_DownloadQueue.Clear();
                _bytesTotalExpected = 0;
                _bytesTotalDownloaded = 0;

                foreach (var kvp in m_PatchList)
                {
                    string key = kvp.Key;

                    // Evitar Data/Data/... en disco si las claves empiezan por Data/
                    string localRel = key.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
                        ? key.Substring("Data/".Length)
                        : key;

                    string localPath = Path.Combine(AppContext.BaseDirectory, "Data", localRel);
                    if (!File.Exists(localPath) || GetMD5(localPath) != kvp.Value)
                    {
                        m_DownloadQueue.Add(key); // en remoto usamos la clave tal cual (puede incluir Data/)
                        long size = await TryGetContentLengthAsync(m_PatchUri + key).ConfigureAwait(false);
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
                    }).ConfigureAwait(false);
                    return;
                }

                // 5) Descarga con progreso global
                IsPatching = true;
                _speedSw.Restart();

                await UI(() =>
                {
                    m_WndRef.progressInfo.Visibility = System.Windows.Visibility.Visible;
                    m_WndRef.playBtn.IsEnabled = false;
                    m_WndRef.progressBar.Value = 0;
                    m_WndRef.progressBar.Maximum = Math.Max((double)_bytesTotalExpected, 1.0);
                }).ConfigureAwait(false);

                for (m_CurrentIndex = 0; m_CurrentIndex < m_DownloadQueue.Count; m_CurrentIndex++)
                {
                    string key = m_DownloadQueue[m_CurrentIndex];

                    string localRel = key.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
                        ? key.Substring("Data/".Length)
                        : key;

                    string url = m_PatchUri + key; // remoto
                    string targetPath = Path.Combine(AppContext.BaseDirectory, "Data", localRel);

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
                                    key,
                                    m_CurrentIndex + 1,
                                    m_DownloadQueue.Count,
                                    ToMB(downloadedSoFarLong),
                                    ToMB(Math.Max(_bytesTotalExpected, downloadedSoFarLong)),
                                    speedStr
                                );
                        });
                    }).ConfigureAwait(false);

                    try { _bytesTotalDownloaded += new FileInfo(targetPath).Length; } catch { }
                }

                _speedSw.Stop();
                IsPatching = false;

                await UI(() =>
                {
                    m_WndRef.progressBar.Value = m_WndRef.progressBar.Maximum;
                    m_WndRef.progressInfo.Text = "Descarga de parches completada.";
                    m_WndRef.playBtn.IsEnabled = true;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IsPatching = false;
                try
                {
                    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Cache", "P"));
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "Cache", "P", "patch_log.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex}\n  PatchListUri: {m_PatchListUri}\n  PatchUri: {m_PatchUri}\n");
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
                }).ConfigureAwait(false);
            }
        }

        // --------- Helpers ---------

        private static string EnsureSlash(string url) => url.EndsWith("/") ? url : (url + "/");

        // GET ligero (Range 0-0) para existencia; más fiable que HEAD con algunos proxies/CDN
        private async Task<bool> UrlExistsAsync(string url)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                        return resp.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        // Intento de obtener Content-Length (si el servidor lo expone)
        private async Task<long> TryGetContentLengthAsync(string url)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                    {
                        if (resp.Content?.Headers?.ContentLength.HasValue == true)
                            return resp.Content.Headers.ContentLength.Value;
                    }
                }
            }
            catch { }
            return 0;
        }

        private async Task DownloadFileAsync(string url, string targetPath, Action<long, TimeSpan> onProgress)
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long bytesThisFile = 0;
                    var sw = Stopwatch.StartNew();

                    while (true)
                    {
                        int read = await src.ReadAsync(buffer, 0, buffer.Length, _cts.Token).ConfigureAwait(false);
                        if (read <= 0) break;

                        await dst.WriteAsync(buffer, 0, read, _cts.Token).ConfigureAwait(false);
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
            return $"{speed:0.00} MB/s";
        }

        private static double ToMB(long bytes) => bytes / (1024.0 * 1024.0);

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
