// PatchController.cs — R2 público (r2.dev), progreso/velocidad/%/restante correctos (.NET Framework/WPF)

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

        // URL pública R2
        private const string R2_PUBLIC_BASE = "https://pub-23b561d8083642d6a10b3ecab65d8834.r2.dev/";

        public PatchController(MainWindow wndRef)
        {
            m_WndRef = wndRef;
            m_PatchList = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            m_DownloadQueue = new List<string>();
            IsPatching = false;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                AllowAutoRedirect = true,
                UseProxy = true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            _cts = new CancellationTokenSource();
        }

        public async void CheckPatch(bool _init = true)
        {
            try
            {
                m_PatchUri = EnsureSlash(R2_PUBLIC_BASE);
                m_PatchListUri = m_PatchUri + "plist.txt";

                if (!await UrlExistsAsync(m_PatchListUri).ConfigureAwait(false))
                    throw new FileNotFoundException("No se encontró plist.txt en la URL pública del bucket R2.\nURL: " + m_PatchListUri);

                string rawData;
                using (var req = new HttpRequestMessage(HttpMethod.Get, m_PatchListUri))
                {
                    req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            throw new HttpRequestException($"No se pudo obtener plist.txt: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                        rawData = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }

                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Cache", "P"));
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "Cache", "P", "plist_debug.txt"), rawData);

                m_PatchList.Clear();
                var lines = rawData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string file = parts[0].Trim();
                    string hash = parts[1].Trim().ToLowerInvariant();
                    m_PatchList[file] = hash;
                }

                m_DownloadQueue.Clear();
                _bytesTotalExpected = 0;
                _bytesTotalDownloaded = 0;

                foreach (var kvp in m_PatchList)
                {
                    string key = kvp.Key;
                    string localRel = key.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
                        ? key.Substring("Data/".Length)
                        : key;
                    string localPath = Path.Combine(AppContext.BaseDirectory, "Data", localRel);

                    bool needByHash = !File.Exists(localPath) || GetMD5(localPath) != kvp.Value;
                    bool needByMeta = false;

                    if (!needByHash && File.Exists(localPath))
                    {
                        var fi = new FileInfo(localPath);
                        long localSize = fi.Length;
                        var localMtimeUtc = fi.LastWriteTimeUtc;

                        var (remoteLen, remoteLm) = await TryGetRemoteMetaAsync(m_PatchUri + key).ConfigureAwait(false);

                        if (remoteLen > 0 && remoteLen != localSize)
                            needByMeta = true;

                        // ⚠️ Descomenta SOLO si tu Last-Modified es estable (algunos CDNs lo cambian en cada request)
                        // if (!needByMeta && remoteLm.HasValue)
                        // {
                        //     var delta = (remoteLm.Value.UtcDateTime - localMtimeUtc).TotalSeconds;
                        //     if (Math.Abs(delta) > 2.0)
                        //         needByMeta = true;
                        // }
                    }

                    if (needByHash || needByMeta)
                    {
                        m_DownloadQueue.Add(key);
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

                IsPatching = true;
                _speedSw.Restart();

                await UI(() =>
                {
                    m_WndRef.progressInfo.Visibility = System.Windows.Visibility.Visible;
                    m_WndRef.playBtn.IsEnabled = false;
                    m_WndRef.progressBar.Value = 0;
                    m_WndRef.progressBar.Maximum = Math.Max((double)_bytesTotalExpected, 1.0);
                    m_WndRef.progressBar.IsIndeterminate = (_bytesTotalExpected == 0);
                }).ConfigureAwait(false);

                for (m_CurrentIndex = 0; m_CurrentIndex < m_DownloadQueue.Count; m_CurrentIndex++)
                {
                    string key = m_DownloadQueue[m_CurrentIndex];
                    string localRel = key.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
                        ? key.Substring("Data/".Length)
                        : key;

                    string url = m_PatchUri + key;
                    string targetPath = Path.Combine(AppContext.BaseDirectory, "Data", localRel);

                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    long fileSoFar = 0;
                    bool fileLengthAppliedToTotal = false;

                    await DownloadFileAsync(url, targetPath, (deltaBytes, elapsed, fileKnownLength) =>
                    {
                        fileSoFar += deltaBytes;

                        if (!fileLengthAppliedToTotal && fileKnownLength.HasValue && fileKnownLength.Value > 0)
                        {
                            _bytesTotalExpected += fileKnownLength.Value;
                            fileLengthAppliedToTotal = true;

                            UI_NoAwait(() =>
                            {
                                m_WndRef.progressBar.IsIndeterminate = false;
                                m_WndRef.progressBar.Maximum = Math.Max((double)_bytesTotalExpected, 1.0);
                            });
                        }

                        long downloadedSoFarLong = _bytesTotalDownloaded + fileSoFar;
                        double maximum = Math.Max((double)_bytesTotalExpected, 1.0);

                        double percent = (_bytesTotalExpected > 0)
                            ? (downloadedSoFarLong / maximum) * 100.0
                            : 0.0;

                        long remaining = (_bytesTotalExpected > downloadedSoFarLong)
                            ? (_bytesTotalExpected - downloadedSoFarLong)
                            : 0;

                        string speedStr = FormatSpeed(deltaBytes, elapsed);

                        UI_NoAwait(() =>
                        {
                            m_WndRef.progressBar.Maximum = maximum;
                            m_WndRef.progressBar.Value = Math.Min(downloadedSoFarLong, (long)maximum);

                            m_WndRef.progressInfo.Text =
                                string.Format(
                                    "{0:0.0}%  •  {1}  ({2}/{3})  Descargado {4:0.0}/{5:0.0} MB  Restante {6:0.0} MB  Velocidad {7}",
                                    percent,
                                    key,
                                    m_CurrentIndex + 1,
                                    m_DownloadQueue.Count,
                                    ToMB(downloadedSoFarLong),
                                    ToMB(_bytesTotalExpected),
                                    ToMB(remaining),
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
                    m_WndRef.progressBar.IsIndeterminate = false;
                    m_WndRef.progressBar.Maximum = Math.Max((double)_bytesTotalExpected, 1.0);
                    m_WndRef.progressBar.Value = m_WndRef.progressBar.Maximum;
                    m_WndRef.progressInfo.Text = "Descarga de parches completada.";
                    m_WndRef.playBtn.IsEnabled = true;
                }).ConfigureAwait(false);

                // Limpieza opcional de huérfanos:
                // PruneFiles(Path.Combine(AppContext.BaseDirectory, "Data"), m_PatchList.Keys);
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

        private async Task<bool> UrlExistsAsync(string url)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                        return resp.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        // ✅ Usa TryGetRemoteMetaAsync (HEAD + fallback) para obtener el tamaño TOTAL real
        private async Task<long> TryGetContentLengthAsync(string url)
        {
            var (len, _) = await TryGetRemoteMetaAsync(url).ConfigureAwait(false);
            return len;
        }

        // ✅ HEAD primero; fallback a GET Range: 0-0 leyendo Content-Range.Length
        private async Task<(long length, DateTimeOffset? lastModified)> TryGetRemoteMetaAsync(string url)
        {
            // 1) HEAD
            try
            {
                using (var headReq = new HttpRequestMessage(HttpMethod.Head, url))
                {
                    headReq.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    using (var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                    {
                        if (headResp.IsSuccessStatusCode)
                        {
                            long len = 0;
                            if (headResp.Content?.Headers?.ContentLength.HasValue == true)
                                len = headResp.Content.Headers.ContentLength.Value;

                            DateTimeOffset? lm = headResp.Content?.Headers?.LastModified;
                            if (lm == null && headResp.Headers.TryGetValues("Last-Modified", out var values))
                            {
                                if (DateTimeOffset.TryParse(string.Join(",", values), out var parsed))
                                    lm = parsed;
                            }

                            if (len > 0 || lm.HasValue)
                                return (len, lm);
                        }
                    }
                }
            }
            catch { /* seguimos al fallback */ }

            // 2) GET Range: 0-0 → leer SIEMPRE Content-Range.Length (no Content-Length del rango)
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return (0, null);

                        long totalLen = 0;

                        var cr = resp.Content?.Headers?.ContentRange;
                        if (cr != null && cr.HasLength && cr.Length.HasValue)
                        {
                            totalLen = cr.Length.Value;
                        }
                        else if (resp.Headers.TryGetValues("Content-Range", out var crVals))
                        {
                            // "bytes 0-0/123456" → 123456
                            var s = string.Join(",", crVals);
                            var slash = s.LastIndexOf('/');
                            if (slash >= 0 && long.TryParse(s.Substring(slash + 1).Trim(), out var parsedTotal))
                                totalLen = parsedTotal;
                        }

                        DateTimeOffset? lm = resp.Content?.Headers?.LastModified;
                        if (lm == null && resp.Headers.TryGetValues("Last-Modified", out var values))
                        {
                            if (DateTimeOffset.TryParse(string.Join(",", values), out var parsed))
                                lm = parsed;
                        }

                        return (totalLen, lm);
                    }
                }
            }
            catch { }

            return (0, null);
        }

        private async Task DownloadFileAsync(
            string url,
            string targetPath,
            Action<long, TimeSpan, long?> onProgress)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url + (url.Contains("?") ? "&" : "?") + "_cb=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            {
                req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();

                    long? fileLen = resp.Content?.Headers?.ContentLength;
                    DateTimeOffset? remoteLm = resp.Content?.Headers?.LastModified;
                    if (remoteLm == null && resp.Headers.TryGetValues("Last-Modified", out var values))
                    {
                        if (DateTimeOffset.TryParse(string.Join(",", values), out var parsed))
                            remoteLm = parsed;
                    }

                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    string tmp = targetPath + ".part";
                    if (File.Exists(tmp)) File.Delete(tmp);

                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                    {
                        var buffer = new byte[1 << 20];
                        long bytesThisFile = 0;
                        long lastReported = 0;
                        var sw = Stopwatch.StartNew();

                        int read;
                        while ((read = await src.ReadAsync(buffer, 0, buffer.Length, _cts.Token).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer, 0, read, _cts.Token).ConfigureAwait(false);
                            bytesThisFile += read;

                            if (sw.ElapsedMilliseconds >= 200)
                            {
                                long delta = bytesThisFile - lastReported;
                                onProgress(delta, sw.Elapsed, fileLen);
                                lastReported = bytesThisFile;
                                sw.Restart();
                            }
                        }

                        long finalDelta = bytesThisFile - lastReported;
                        if (finalDelta > 0)
                            onProgress(finalDelta, TimeSpan.FromMilliseconds(250), fileLen);
                    }

                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    File.Move(tmp, targetPath);

                    if (remoteLm.HasValue)
                        File.SetLastWriteTimeUtc(targetPath, remoteLm.Value.UtcDateTime);
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

        private void PruneFiles(string dataRoot, IEnumerable<string> manifestKeys)
        {
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in manifestKeys)
            {
                string rel = key.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) ? key.Substring(5) : key;
                expected.Add(Path.GetFullPath(Path.Combine(dataRoot, rel)));
            }

            foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                if (!expected.Contains(full) && (file.EndsWith(".MPQ", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".patch", StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(full); } catch { }
                }
            }
        }

        private Task UI(Action a) => m_WndRef.Dispatcher.InvokeAsync(a).Task;
        private void UI_NoAwait(Action a) => m_WndRef.Dispatcher.Invoke(a);
    }
}
