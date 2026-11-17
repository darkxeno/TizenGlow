using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
                    if (IsAlreadyRunning())
                    {
                        _status = "running (existing instance)";
                        AppLog.Info("VpBridge", "vp_bridge already running; skipping launch");
                        return;
                    }

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

                    bool needsCopy = !File.Exists(binPath) ||
                                     new FileInfo(resPath).Length != new FileInfo(binPath).Length ||
                                     File.GetLastWriteTimeUtc(resPath) > File.GetLastWriteTimeUtc(binPath);

                    if (needsCopy)
                    {
                        File.Copy(resPath, binPath, overwrite: true);
                    }

                    chmod(binPath, 0x1ED); // 0755

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
                        _status = "running (pid=" + _childProcess.Id + ")";
                        AppLog.Info("VpBridge", "Started user-mode vp_bridge pid=" + _childProcess.Id);
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

        public static bool IsAlreadyRunning()
        {
            // 1. Si ya llegan datagramas TGVP por UDP :19446, hay una instancia activa
            if (TgvpUdpReceiver.IsActive)
                return true;

            // 2. Si el proceso hijo lanzado por nosotros sigue vivo
            if (_childProcess != null)
            {
                try
                {
                    if (!_childProcess.HasExited)
                        return true;
                }
                catch { }
            }

            // 3. Escanear /proc buscando una instancia previa (incluso en reposo sin emitir UDP)
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
            catch
            {
            }

            return false;
        }

        public static void StopOwned()
        {
            lock (_lock)
            {
                if (_childProcess == null) return;
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
                    _status = "stopped";
                }
            }
        }
    }
}