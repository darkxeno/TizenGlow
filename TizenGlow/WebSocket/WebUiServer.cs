using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TizenGlow.WebSocket
{
    /// <summary>
    /// Serves the embedded TizenGlow web UI over HTTP (default port 8090) so the TV service
    /// works standalone from any browser or WebView without TizenBrew.
    /// </summary>
    public sealed class WebUiServer
    {
        public const int DefaultPort = 8090;

        private readonly HttpListener _listener;
        private readonly Assembly _assembly;

        public WebUiServer(string uriPrefix = "http://+:8090/", Assembly resourceAssembly = null)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(uriPrefix);
            _assembly = resourceAssembly ?? typeof(WebUiServer).Assembly;
        }

        public static bool TryResolveResource(string rawPath, out string resourceName, out string contentType)
        {
            string path = (rawPath ?? "/").Split('?')[0].Trim();
            if (path.Length == 0 || path == "/") path = "/index.html";

            switch (path.ToLowerInvariant())
            {
                case "/index.html":
                    resourceName = "TizenGlowUI.index.html";
                    contentType = "text/html; charset=utf-8";
                    return true;
                case "/main.css":
                    resourceName = "TizenGlowUI.main.css";
                    contentType = "text/css; charset=utf-8";
                    return true;
                case "/icon.png":
                    resourceName = "TizenGlowUI.icon.png";
                    contentType = "image/png";
                    return true;
                case "/js/wsclient.js":
                    resourceName = "TizenGlowUI.js.wsClient.js";
                    contentType = "application/javascript; charset=utf-8";
                    return true;
                case "/js/calibration.js":
                    resourceName = "TizenGlowUI.js.calibration.js";
                    contentType = "application/javascript; charset=utf-8";
                    return true;
                default:
                    resourceName = null;
                    contentType = null;
                    return false;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _listener.Start();
            AppLog.Info("WebUiServer", "Listening for HTTP UI requests");
            using (cancellationToken.Register(() => { try { _listener.Stop(); } catch { } }))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync();
                    }
                    catch when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException ex)
                    {
                        AppLog.Error("WebUiServer", ex);
                        break;
                    }
                    _ = Task.Run(() => HandleRequestAsync(ctx));
                }
            }
        }

        public void Stop()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = (ctx.Request.RawUrl ?? "/").Split('?')[0].Trim();
                if (string.Equals(path, "/logs", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] body = System.Text.Encoding.UTF8.GetBytes(AppLog.Dump());
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    ctx.Response.Headers["Cache-Control"] = "no-cache";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body, 0, body.Length);
                    ctx.Response.OutputStream.Close();
                    return;
                }

                string resourceName, contentType;
                if (!TryResolveResource(ctx.Request.RawUrl, out resourceName, out contentType))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                using (Stream stream = _assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        return;
                    }

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = contentType;
                    ctx.Response.Headers["Cache-Control"] = "no-cache";
                    ctx.Response.ContentLength64 = stream.Length;
                    await stream.CopyToAsync(ctx.Response.OutputStream);
                    ctx.Response.OutputStream.Close();
                }
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }
    }
}
