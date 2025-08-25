// Program.cs (Updater) — v3: ignora Updater.exe y espera cierre/desbloqueo
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;

namespace Updater
{
    internal static class Program
    {
        private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, "Cache", "U");
        private const int MaxWaitMsForExit = 60_000;   // 60s para que el launcher muera
        private const int MaxWaitMsForUnlock = 60_000; // 60s para que se desbloqueen ficheros
        private const int PollMs = 250;

        [STAThread]
        private static void Main(string[] args)
        {
            Directory.CreateDirectory(CacheDir);
            string logFile = Path.Combine(CacheDir, "updater_log.txt");

            try
            {
                File.AppendAllText(logFile, $"[{DateTime.Now}] Updater iniciado.\n");

                // 0) Espera a que el launcher termine y a que su EXE esté desbloqueado
                string launcherExe = Path.Combine(AppContext.BaseDirectory, "WoWLauncher.exe");
                int? parentPid = TryParsePidArg(args);
                WaitLauncherExit(parentPid, logFile);
                WaitFileUnlocked(launcherExe, MaxWaitMsForUnlock, logFile);

                // 1) Leer versión
                string versionFile = Path.Combine(AppContext.BaseDirectory, "Cache", "L", "version.txt");
                if (!File.Exists(versionFile))
                {
                    File.AppendAllText(logFile, "No se encontró version.txt, cancelando update.\n");
                    return;
                }

                string ver = File.ReadAllText(versionFile).Trim(); // ej. "1.2"
                string tag = "v" + ver;                            // ej. "v1.2"

                // 2) Descargar zip
                string zipUrl = $"https://92e01281701b8cee8449177420951cf8.r2.cloudflarestorage.com/patcher/client.zip";
                string zipPath = Path.Combine(CacheDir, "client.zip");
                File.AppendAllText(logFile, $"Descargando desde: {zipUrl}\n");

                using (var wc = new WebClient())
                    wc.DownloadFile(zipUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), zipPath);

                // 3) Extraer (saltando Updater.exe / archivos del propio updater)
                string baseDir = AppContext.BaseDirectory;
                File.AppendAllText(logFile, $"Extrayendo en: {baseDir}\n");

                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // Nombres a ignorar (no podemos sobrescribirnos a nosotros mismos)
                        string fileName = Path.GetFileName(entry.FullName);
                        if (string.Equals(fileName, "Updater.exe", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, "Updater.pdb", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, "Updater.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            File.AppendAllText(logFile, $"Saltando {entry.FullName} (archivo del updater en uso).\n");
                            continue;
                        }

                        string destinationPath = Path.Combine(baseDir, entry.FullName);
                        string? directoryPath = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
                            Directory.CreateDirectory(directoryPath);

                        if (!string.IsNullOrEmpty(entry.Name))
                        {
                            // Para ficheros críticos, asegura desbloqueo
                            if (string.Equals(Path.GetFileName(destinationPath), "WoWLauncher.exe", StringComparison.OrdinalIgnoreCase))
                                WaitFileUnlocked(destinationPath, MaxWaitMsForUnlock, logFile);

                            entry.ExtractToFile(destinationPath, true);
                        }
                    }
                }

                File.AppendAllText(logFile, "Extracción completada.\n");

                // 4) Relanzar launcher
                if (File.Exists(launcherExe))
                {
                    File.AppendAllText(logFile, "Relanzando WoWLauncher.exe...\n");
                    Thread.Sleep(1000);
                    Process.Start(new ProcessStartInfo(launcherExe) { UseShellExecute = true });
                }
                else
                {
                    File.AppendAllText(logFile, "No se encontró WoWLauncher.exe tras update.\n");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(logFile, $"Error en Updater: {ex}\n");
            }
        }

        private static int? TryParsePidArg(string[] args)
        {
            if (args != null && args.Length > 0 && int.TryParse(args[0], out int pid))
                return pid;
            return null;
        }

        private static void WaitLauncherExit(int? parentPid, string logFile)
        {
            try
            {
                DateTime start = DateTime.Now;

                if (parentPid.HasValue)
                {
                    try
                    {
                        var p = Process.GetProcessById(parentPid.Value);
                        if (!p.HasExited)
                        {
                            File.AppendAllText(logFile, $"Esperando a que el launcher (PID {parentPid}) termine...\n");
                            p.WaitForExit(MaxWaitMsForExit);
                        }
                    }
                    catch { /* si ya no existe, ok */ }
                }

                // respaldo por nombre
                string baseName = "WoWLauncher";
                while (Process.GetProcessesByName(baseName).Length > 0 &&
                       (DateTime.Now - start).TotalMilliseconds <= MaxWaitMsForExit)
                {
                    Thread.Sleep(PollMs);
                }

                File.AppendAllText(logFile, "Comprobación de cierre del launcher terminada.\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logFile, $"[WaitLauncherExit] aviso: {ex}\n");
            }
        }

        private static void WaitFileUnlocked(string path, int timeoutMs, string logFile)
        {
            if (string.IsNullOrEmpty(path)) return;
            DateTime start = DateTime.Now;

            while (true)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        fs.Close(); // si abre en exclusivo, ya está libre
                        return;
                    }
                }
                catch
                {
                    if (!File.Exists(path)) return; // no existe todavía → no hay bloqueo
                    if ((DateTime.Now - start).TotalMilliseconds > timeoutMs)
                    {
                        File.AppendAllText(logFile, $"[WaitFileUnlocked] Timeout esperando desbloqueo de: {path}\n");
                        return;
                    }
                    Thread.Sleep(PollMs);
                }
            }
        }
    }
}
