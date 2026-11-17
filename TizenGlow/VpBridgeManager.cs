using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Tizen.Applications;

namespace TizenGlow
{
    internal static class VpBridgeManager
    {
        private static readonly object _lock = new object();
        private static Process _childProcess;
        private static volatile string _status = "not started";

        public static string Status => _status;

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int chmod(string pathname, int mode);

        public static Task EnsureRunningAsync()
        {
            return Task.Run(() => EnsureRunning());
        }

        public static void EnsureRunning()
        {
            lock (_lock)
            {
                try
                {
                    string resPath = Path.Combine(Application.Current.DirectoryInfo.Resource, "vp_bridge");
                    if (!File.Exists(resPath))
                    {
                        _status = "binary missing in res/";
                        AppLog.Info("VpBridge", "Bundled vp_bridge not found at " + resPath);
                        return;
                    }

                    string dataDir = Application.Current.DirectoryInfo.Data;
                    Directory.CreateDirectory(dataDir);
                    string binPath = Path.Combine(dataDir, "vp_bridge");
                    string logPath = Path.Combine(dataDir, "vp_bridge.log");

                    string resHash = ComputeSha256(resPath);
                    string binHash = File.Exists(binPath) ? ComputeSha256(binPath) : null;
                    bool binaryUpdated = !string.Equals(resHash, binHash, StringComparison.OrdinalIgnoreCase);

                    if (binaryUpdated)
                    {
                        AppLog.Info("VpBridge", $"New vp_bridge binary detected (sha256={resHash.Substring(0, 12)}), stopping owned instances");
                        StopOwnedInternal(binPath);
                        File.Copy(resPath, binPath, overwrite: true);
                        chmod(binPath, 0x1ED); // 0755
                    }
                    else
                    {
                        chmod(binPath, 0x1ED);
                        if (IsAlreadyRunning())
                        {
                            _status = "running (existing instance)";
                            AppLog.Info("VpBridge", "vp_bridge already running and up-to-date; skipping launch");
                            return;
                        }
                    }

                    // Si hay una instancia externa (ej. /run/vp_bridge como root) enviando UDP, no duplicar
                    if (TgvpUdpReceiver.IsActive)
                    {
                        _status = "running (external instance)";
                        AppLog.Info("VpBridge", "External TGVP UDP source active; skipping user-mode launch");
                        return;
                    }

                    string ver = ReadBinaryVersion(binPath);
                    var psi = new ProcessStartInfo
                    {
                        FileName = binPath,
                        Arguments = $"--lib /usr/lib/libvideo-capture.so.0.1.0 --fps 30 --verbose --log \"{logPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        WorkingDirectory = dataDir
                    };

                    _childProcess = Process.Start(psi);
                    if (_childProcess != null && !_childProcess.HasExited)
                    {
                        _status = $"running ({ver}, pid={_childProcess.Id})";
                        AppLog.Info("VpBridge", $"Started user-mode {ver} pid={_childProcess.Id}");
                    }
                    else
                    {
                        _status = "exited immediately";
                        AppLog.Info("VpBridge", "vp_bridge exited immediately; check " + logPath);
                    }
                }
                catch (Exception ex)
                {
                    _status = "error: " + ex.Message;
                    AppLog.Error("VpBridge", ex);
                }
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ReadBinaryVersion(string binPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = binPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null && p.WaitForExit(1000))
                    {
                        string outText = p.StandardOutput.ReadToEnd().Trim();
                        if (!string.IsNullOrEmpty(outText)) return outText;
                    }
                }
            }
            catch { }
            return "vp_bridge";
        }

        public static bool IsAlreadyRunning()
        {
            if (TgvpUdpReceiver.IsActive)
                return true;

            if (_childProcess != null)
            {
                try
                {
                    if (!_childProcess.HasExited)
                        return true;
                }
                catch { }
            }

            try
            {
                int myPid = Process.GetCurrentProcess().Id;
                foreach (string dir in Directory.EnumerateDirectories("/proc"))
                {
                    string name = Path.GetFileName(dir);
                    if (!int.TryParse(name, out int pid) || pid == myPid)
                        continue;

                    string cmdlinePath = Path.Combine(dir, "cmdline");
                    if (!File.Exists(cmdlinePath))
                        continue;

                    string cmdline = File.ReadAllText(cmdlinePath).Replace('\0', ' ');
                    if (cmdline.Contains("vp_bridge") || cmdline.Contains("tzcapturesample"))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static void StopOwnedInternal(string ownedBinPath)
        {
            if (_childProcess != null)
            {
                try
                {
                    if (!_childProcess.HasExited)
                    {
                        _childProcess.Kill();
                        _childProcess.WaitForExit(1000);
                    }
                }
                catch { }
                finally
                {
                    _childProcess.Dispose();
                    _childProcess = null;
                }
            }

            // Matar también procesos huérfanos en modo usuario ejecutándose desde Data/vp_bridge
            try
            {
                int myPid = Process.GetCurrentProcess().Id;
                foreach (string dir in Directory.EnumerateDirectories("/proc"))
                {
                    string name = Path.GetFileName(dir);
                    if (!int.TryParse(name, out int pid) || pid == myPid)
                        continue;

                    string cmdlinePath = Path.Combine(dir, "cmdline");
                    bool isOwned = false;

                    try
                    {
                        if (File.Exists(cmdlinePath))
                        {
                            string cmd = File.ReadAllText(cmdlinePath).Replace('\0', ' ');
                            if (cmd.Contains(ownedBinPath))
                                isOwned = true;
                        }
                    }
                    catch { }

                    if (isOwned)
                    {
                        try
                        {
                            using (var proc = Process.GetProcessById(pid))
                            {
                                proc.Kill();
                                proc.WaitForExit(1000);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void StopOwned()
        {
            lock (_lock)
            {
                string dataDir = Application.Current.DirectoryInfo.Data;
                string binPath = Path.Combine(dataDir, "vp_bridge");
                StopOwnedInternal(binPath);
                _status = "stopped";
            }
        }
    }
}