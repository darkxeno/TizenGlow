using System;
using System.Threading.Tasks;
using ElmSharp;
using Tizen.Applications;
using Tizen.WebView;

namespace TizenGlowUIApp
{
    internal sealed class UiApp : CoreUIApplication
    {
        private const string ServiceAppId = "io.gh.darkxeno.TizenGlow";
        private Window _window;
        private WebView _webView;

        protected override void OnCreate()
        {
            base.OnCreate();
            EnsureServiceRunning();
            InitializeUi();
        }

        private static void EnsureServiceRunning()
        {
            try
            {
                AppControl.SendLaunchRequest(new AppControl { ApplicationId = ServiceAppId });
            }
            catch { }
        }

        private void InitializeUi()
        {
            Elementary.Initialize();
            Elementary.ThemeOverlay();

            _window = new Window("TizenGlow")
            {
                AvailableRotations = DisplayRotation.Degree_0
            };
            _window.BackButtonPressed += (s, e) => Exit();
            _window.Show();

            var conformant = new Conformant(_window);
            conformant.Show();

            Chromium.Initialize();
            _webView = new WebView(_window)
            {
                AlignmentX = -1,
                AlignmentY = -1,
                WeightX = 1,
                WeightY = 1
            };
            _webView.Show();
            conformant.SetContent(_webView);

            // Give the background service a brief moment if it just started before loading 127.0.0.1:8090
            _webView.LoadError += (s, e) =>
            {
                Task.Delay(1000).ContinueWith(_ =>
                {
                    EcoreMainloop.Post(() =>
                    {
                        try { _webView?.LoadUrl("http://127.0.0.1:8090/"); } catch { }
                    });
                });
            };

            _webView.LoadUrl("http://127.0.0.1:8090/");
            _webView.SetFocus(true);
        }

        protected override void OnTerminate()
        {
            try { _webView?.Unrealize(); } catch { }
            base.OnTerminate();
        }

        private static void Main(string[] args)
        {
            var app = new UiApp();
            app.Run(args);
        }
    }
}
