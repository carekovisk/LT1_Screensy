using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScreensyWindowAudio;

public sealed class MainForm : Form
{
    private const string ScreensyUrl = "https://screensy.marijn.it/";
    private const int MinimumWindowAudioRuntimeMajor = 141;

    private readonly WebView2 _webView = new();
    private readonly ComboBox _audioMode = new();
    private readonly Label _runtimeLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Button _reloadButton = new();
    private readonly Button _copyShareLinkButton = new();
    private readonly Button _openBrowserButton = new();

    private string? _injectedScriptId;
    private bool _initialized;

    public MainForm()
    {
        Text = "LT1 - Screensy - 0.1";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(900, 620);

        BuildUi();
        Shown += async (_, _) => await InitializeWebViewAsync();
    }

    private void BuildUi()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 7, 8, 5),
            AutoSize = false
        };

        var audioLabel = new Label
        {
            Text = "Audio:",
            AutoSize = true,
            Margin = new Padding(0, 7, 5, 0)
        };

        _audioMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _audioMode.Width = 150;
        _audioMode.Items.AddRange(new object[]
        {
            "Window only",
            "System audio",
            "No audio"
        });
        _audioMode.SelectedIndex = 0;
        _audioMode.SelectedIndexChanged += async (_, _) =>
        {
            if (_initialized)
                await ReinjectAndReloadAsync();
        };

        _reloadButton.Text = "Reload";
        _reloadButton.AutoSize = true;
        _reloadButton.Click += (_, _) => _webView.Reload();

        _copyShareLinkButton.Text = "Copy Share Link";
        _copyShareLinkButton.AutoSize = true;
        _copyShareLinkButton.Click += (_, _) => CopyShareLink();

        _openBrowserButton.Text = "Open in Edge";
        _openBrowserButton.AutoSize = true;
        _openBrowserButton.Click += (_, _) => OpenExternal(ScreensyUrl);
        _openBrowserButton.Visible = false; // Hide this button for now, as it may not be necessary for most users.

        _runtimeLabel.AutoSize = true;
        _runtimeLabel.Margin = new Padding(14, 7, 0, 0);
        _runtimeLabel.Text = "WebView2: checking...";

        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(14, 7, 0, 0);
        _statusLabel.Text = "Starting...";

        toolbar.Controls.Add(audioLabel);
        toolbar.Controls.Add(_audioMode);
        toolbar.Controls.Add(_reloadButton);
        toolbar.Controls.Add(_copyShareLinkButton);
        toolbar.Controls.Add(_openBrowserButton);
        toolbar.Controls.Add(_runtimeLabel);
        toolbar.Controls.Add(_statusLabel);

        _webView.Dock = DockStyle.Fill;

        Controls.Add(_webView);
        Controls.Add(toolbar);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            _statusLabel.Text = "Initializing WebView2...";
            await _webView.EnsureCoreWebView2Async();

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.IsZoomControlEnabled = true;

            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;

            var version = core.Environment.BrowserVersionString;
            _runtimeLabel.Text = $"WebView2: {version}";

            var major = ParseMajorVersion(version);
            if (major is not null && major < MinimumWindowAudioRuntimeMajor)
            {
                _statusLabel.Text = $"Window audio needs WebView2 {MinimumWindowAudioRuntimeMajor}+";
                MessageBox.Show(
                    $"Your Microsoft Edge WebView2 Runtime is version {version}.\n\n" +
                    $"Window-only audio requires runtime {MinimumWindowAudioRuntimeMajor} or newer. " +
                    "Update Microsoft Edge / WebView2 Runtime and reopen this app.",
                    "WebView2 Runtime too old",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            await InstallCaptureOverrideAsync();
            _initialized = true;
            core.Navigate(ScreensyUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowRuntimeMissingError();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Initialization failed";
            MessageBox.Show(
                ex.ToString(),
                "Screensy Window Audio - startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task ReinjectAndReloadAsync()
    {
        if (_webView.CoreWebView2 is null)
            return;

        _statusLabel.Text = "Applying audio mode...";
        await InstallCaptureOverrideAsync();
        _webView.CoreWebView2.Navigate(ScreensyUrl);
    }

    private async Task InstallCaptureOverrideAsync()
    {
        var core = _webView.CoreWebView2;
        if (core is null)
            return;

        if (!string.IsNullOrWhiteSpace(_injectedScriptId))
        {
            core.RemoveScriptToExecuteOnDocumentCreated(_injectedScriptId);
            _injectedScriptId = null;
        }

        var mode = (AudioMode)_audioMode.SelectedIndex;
        var script = BuildInjectionScript(mode);
        _injectedScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private static string BuildInjectionScript(AudioMode mode)
    {
        var captureOptions = mode switch
        {
            AudioMode.WindowOnly => "audio: true, windowAudio: 'window', systemAudio: 'exclude'",
            AudioMode.SystemAudio => "audio: true, windowAudio: 'system', systemAudio: 'include'",
            AudioMode.NoAudio => "audio: false, windowAudio: 'exclude', systemAudio: 'exclude'",
            _ => "audio: true, windowAudio: 'window', systemAudio: 'exclude'"
        };

        return $$"""
        (() => {
            if (location.hostname !== 'screensy.marijn.it') return;

            const mediaDevices = navigator.mediaDevices;
            if (!mediaDevices || typeof mediaDevices.getDisplayMedia !== 'function') return;
            if (mediaDevices.__screensyWindowAudioPatched) return;

            const original = mediaDevices.getDisplayMedia.bind(mediaDevices);

            Object.defineProperty(mediaDevices, '__screensyWindowAudioPatched', {
                value: true,
                configurable: false,
                enumerable: false,
                writable: false
            });

            mediaDevices.getDisplayMedia = function(options = {}) {
                const requested = (options && typeof options === 'object') ? options : {};
                const merged = {
                    ...requested,
                    {{captureOptions}}
                };

                console.debug('[Screensy Window Audio] getDisplayMedia options:', merged);
                return original(merged);
            };
        })();
        """;
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _statusLabel.Text = "Loading...";

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            return;

        if (uri.Scheme is "about" or "data" or "blob")
            return;

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("screensy.marijn.it", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _statusLabel.Text = e.IsSuccess
            ? SelectedModeStatusText()
            : $"Navigation error: {e.WebErrorStatus}";
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private void CopyShareLink()
    {
        var core = _webView.CoreWebView2;
        if (core is null)
        {
            _statusLabel.Text = "Screensy is not ready yet";
            return;
        }

        var currentUrl = core.Source;
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("screensy.marijn.it", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Fragment))
        {
            _statusLabel.Text = "Share link is not ready yet";
            MessageBox.Show(
                "Screensy has not created the room link yet. Wait until the page finishes loading, then try again.",
                "Share link not ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Clipboard.SetText(currentUrl);
            _statusLabel.Text = $"Share link copied: {uri.Fragment.TrimStart('#')}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Could not copy share link";
            MessageBox.Show(
                $"Could not copy the share link to the clipboard.\n\n{ex.Message}",
                "Clipboard error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private string SelectedModeStatusText() => (AudioMode)_audioMode.SelectedIndex switch
    {
        AudioMode.WindowOnly => "Window-only audio enabled",
        AudioMode.SystemAudio => "System audio enabled",
        AudioMode.NoAudio => "Audio disabled",
        _ => "Ready"
    };

    private static int? ParseMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var first = version.Split('.', '-', '+')[0];
        return int.TryParse(first, out var major) ? major : null;
    }

    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore external browser launch failures.
        }
    }

    private void InitializeComponent()
    {

    }

    private void ShowRuntimeMissingError()
    {
        _statusLabel.Text = "WebView2 Runtime not installed";

        var result = MessageBox.Show(
            "Microsoft Edge WebView2 Runtime is not installed.\n\n" +
            "It is included with current Windows 11 installations and Microsoft Edge, " +
            "but can also be installed separately.\n\nOpen the Microsoft download page?",
            "WebView2 Runtime required",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
            OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
    }

    private enum AudioMode
    {
        WindowOnly = 0,
        SystemAudio = 1,
        NoAudio = 2
    }
}
