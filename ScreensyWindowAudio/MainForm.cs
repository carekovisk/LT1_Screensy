using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScreensyWindowAudio;

public sealed class MainForm : Form
{
    private const string ScreensyUrl = "https://screensy.marijn.it/";
    private const int MinimumWindowAudioRuntimeMajor = 141;

    // LT1 Direct: docs/index.html is embedded in the exe and served locally to the broadcaster
    // under this virtual host; viewers open the same page from GitHub Pages.
    private const string DirectHost = "lt1.example";
    private const string DirectUrl = "https://" + DirectHost + "/";
    private const string ViewerBaseUrl = "https://carekovisk.github.io/lt1_stream/";

    private static readonly string[] PresetUrls =
    {
        DirectUrl,
        "https://screensy.marijn.it/",
        "https://screensharing.net/"
    };

    // Hosts the embedded browser may navigate to; anything else opens in the default browser.
    // Hosts typed into the address box are added at runtime.
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        DirectHost,
        "screensy.marijn.it",
        "screensharing.net"
    };

    private readonly WebView2 _webView = new();
    private readonly ComboBox _urltext = new();
    private readonly Button _go = new();
    private readonly ComboBox _audioMode = new();
    private readonly ComboBox _qualityMode = new();
    private readonly Label _runtimeLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Button _reloadButton = new();
    private readonly Button _copyShareLinkButton = new();
    private readonly Button _openBrowserButton = new();

    private string? _injectedScriptId;
    private bool _userEditingUrl;
    private bool _initialized;

    public MainForm()
    {
        Text = "Lightone Stream - 1.0";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(900, 620);

        BuildUi();
        Shown += async (_, _) => await InitializeWebViewAsync();
    }

    private void BuildUi()
    {
        // Row 1: address box (stretches) + Go button.
        var addressBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 7, 8, 0)
        };
        addressBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _urltext.DropDownStyle = ComboBoxStyle.DropDown;
        _urltext.Dock = DockStyle.Fill;
        _urltext.Margin = new Padding(0, 1, 5, 0);
        _urltext.Font = new Font(_urltext.Font, FontStyle.Bold);
        _urltext.Items.AddRange(PresetUrls);
        _urltext.Text = DirectUrl;
        // TextUpdate fires only for user typing, not for programmatic Text changes.
        _urltext.TextUpdate += (_, _) => _userEditingUrl = true;
        _urltext.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape)
                return;

            // Discard the edit and show the current page address again.
            _userEditingUrl = false;
            _urltext.Text = _webView.CoreWebView2?.Source ?? _urltext.Text;
            e.SuppressKeyPress = true;
        };
        _urltext.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.Handled = true;
            e.SuppressKeyPress = true; // no "ding"
            NavigateToAddress();
        };

        // Picking a preset from the list navigates immediately.
        // (Text is not updated yet when this fires, so use SelectedItem.)
        _urltext.SelectionChangeCommitted += (_, _) =>
        {
            if (_urltext.SelectedItem is string url)
                NavigateToAddress(url);
        };

        _go.Text = "Go";
        _go.AutoSize = true;
        _go.Margin = new Padding(0);
        _go.Click += (_, _) => NavigateToAddress();

        addressBar.Controls.Add(_urltext, 0, 0);
        addressBar.Controls.Add(_go, 1, 0);

        // Row 2: existing controls.
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

        var qualityLabel = new Label
        {
            Text = "Quality:",
            AutoSize = true,
            Margin = new Padding(10, 7, 5, 0)
        };

        // Order must match QualityPreset.
        _qualityMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _qualityMode.Width = 170;
        _qualityMode.Items.AddRange(new object[]
        {
            "Fluido (60 fps)",
            "Nítido (30 fps)",
            "Padrão",
            "Leve (720p)"
        });
        _qualityMode.SelectedIndex = (int)QualityPreset.Padrao;
        _qualityMode.SelectedIndexChanged += async (_, _) =>
        {
            if (_initialized)
                await ApplyQualityAsync();
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
        toolbar.Controls.Add(qualityLabel);
        toolbar.Controls.Add(_qualityMode);
        toolbar.Controls.Add(_reloadButton);
        toolbar.Controls.Add(_copyShareLinkButton);
        toolbar.Controls.Add(_openBrowserButton);
        toolbar.Controls.Add(_runtimeLabel);
        toolbar.Controls.Add(_statusLabel);

        _webView.Dock = DockStyle.Fill;

        // Docking order: the last added Top control ends up on top.
        Controls.Add(_webView);
        Controls.Add(toolbar);
        Controls.Add(addressBar);
    }

    private void NavigateToAddress(string? address = null)
    {
        var core = _webView.CoreWebView2;
        if (core is null)
            return;

        var text = (address ?? _urltext.Text).Trim();
        if (text.Length == 0)
            return;

        if (!text.Contains("://"))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _statusLabel.Text = "Invalid address (https only)";
            return;
        }

        // The user explicitly asked for this site, so let it load inside the wrapper.
        _allowedHosts.Add(uri.Host);
        _userEditingUrl = false; // let the page's address (e.g. #Room) show up again
        core.Navigate(uri.AbsoluteUri);
    }

    private bool IsAllowedHost(string host) =>
        _allowedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                               host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

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
            core.WebMessageReceived += Core_WebMessageReceived;
            core.SourceChanged += (_, _) =>
            {
                // Don't overwrite what the user is typing.
                if (!_userEditingUrl)
                    _urltext.Text = core.Source;
            };

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

            core.AddWebResourceRequestedFilter(DirectUrl + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Core_DirectPageRequested;

            await InstallCaptureOverrideAsync();
            _initialized = true;
            core.Navigate(DirectUrl);
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
                "Lightone Stream - startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // Serves the embedded broadcaster page for https://lt1.example/ straight from the exe
    // (no files on disk). Any other path on that host is a 404.
    private void Core_DirectPageRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = _webView.CoreWebView2;
        var path = new Uri(e.Request.Uri).AbsolutePath;

        if (path is not ("/" or "/index.html"))
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        var resource = typeof(MainForm).Assembly.GetManifestResourceStream("web.index.html");
        e.Response = resource is null
            ? core.Environment.CreateWebResourceResponse(null, 500, "Missing embedded page", "")
            : core.Environment.CreateWebResourceResponse(
                resource, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    private async Task ReinjectAndReloadAsync()
    {
        if (_webView.CoreWebView2 is null)
            return;

        _statusLabel.Text = "Applying audio mode...";
        await InstallCaptureOverrideAsync();
        _webView.CoreWebView2.Reload(); // stay on the current site/room
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
        var quality = (QualityPreset)_qualityMode.SelectedIndex;
        var script = BuildInjectionScript(mode, quality);
        _injectedScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private async Task ApplyQualityAsync()
    {
        var core = _webView.CoreWebView2;
        if (core is null)
            return;

        // Future documents (reload/navigation) get the new preset from the injected script...
        await InstallCaptureOverrideAsync();

        // ...and the current page applies it live to the running capture and connections.
        var json = QualityJson((QualityPreset)_qualityMode.SelectedIndex);
        await core.ExecuteScriptAsync($"window.__lt1SetQuality && window.__lt1SetQuality({json});");
        _statusLabel.Text = $"Quality: {_qualityMode.SelectedItem}";
    }

    // null = Padrão: leave the site's/browser's defaults untouched.
    private static string QualityJson(QualityPreset preset) => preset switch
    {
        QualityPreset.Fluido => """{"fps":60,"maxWidth":1920,"maxHeight":1080,"maxBitrate":8000000,"contentHint":"motion","degradation":"maintain-framerate"}""",
        QualityPreset.Nitido => """{"fps":30,"maxWidth":1920,"maxHeight":1080,"maxBitrate":6000000,"contentHint":"detail","degradation":"maintain-resolution"}""",
        QualityPreset.Leve => """{"fps":30,"maxWidth":1280,"maxHeight":720,"maxBitrate":1500000,"contentHint":"motion","degradation":"balanced"}""",
        _ => "null"
    };

    private static string BuildInjectionScript(AudioMode mode, QualityPreset quality)
    {
        var qualityJson = QualityJson(quality);

        var captureOptions = mode switch
        {
            AudioMode.WindowOnly => "audio: true, windowAudio: 'window', systemAudio: 'exclude'",
            AudioMode.SystemAudio => "audio: true, windowAudio: 'system', systemAudio: 'include'",
            AudioMode.NoAudio => "audio: false, windowAudio: 'exclude', systemAudio: 'exclude'",
            _ => "audio: true, windowAudio: 'window', systemAudio: 'exclude'"
        };

        return $$"""
        (() => {
            // Navigation is already restricted to allowed hosts by the wrapper.
            if (location.protocol !== 'https:') return;

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

            // ---- Quality presets -------------------------------------------------
            let quality = {{qualityJson}};
            let qualityTouched = quality !== null; // once changed, Padrão must undo overrides
            const videoTracks = new Set();
            const peerConnections = new Set();

            const post = (message) => {
                try { window.chrome?.webview?.postMessage(message); } catch { }
            };

            const videoConstraints = (q) => {
                if (!q) return {};
                const c = { frameRate: { ideal: q.fps, max: q.fps } };
                if (q.maxWidth) c.width = { max: q.maxWidth };
                if (q.maxHeight) c.height = { max: q.maxHeight };
                return c;
            };

            const reportCapture = () => {
                const track = [...videoTracks].find(t => t.readyState === 'live');
                if (!track) return;
                const s = track.getSettings();
                post({ type: 'capture', width: s.width, height: s.height, frameRate: s.frameRate });
            };

            const applyToTrack = async (track) => {
                if (track.readyState === 'ended') { videoTracks.delete(track); return; }
                track.contentHint = quality ? quality.contentHint : '';
                try { await track.applyConstraints(videoConstraints(quality)); }
                catch (e) { console.warn('[LT1] applyConstraints failed', e); }
            };

            const setSenderParameters = async (sender, withDegradation) => {
                const p = sender.getParameters();
                if (!p.encodings || p.encodings.length === 0) return;
                for (const enc of p.encodings) {
                    if (quality) {
                        enc.maxBitrate = quality.maxBitrate;
                        enc.maxFramerate = quality.fps;
                    } else {
                        delete enc.maxBitrate;
                        delete enc.maxFramerate;
                    }
                }
                if (withDegradation && quality) p.degradationPreference = quality.degradation;
                else delete p.degradationPreference;
                await sender.setParameters(p);
            };

            const applyToPeerConnection = async (pc) => {
                if (pc.connectionState === 'closed') { peerConnections.delete(pc); return; }
                if (!qualityTouched) return;
                for (const sender of pc.getSenders()) {
                    if (!sender.track || sender.track.kind !== 'video') continue;
                    try { await setSenderParameters(sender, true); }
                    catch {
                        // Older runtimes may reject degradationPreference; retry without it.
                        try { await setSenderParameters(sender, false); }
                        catch (e) { console.warn('[LT1] setParameters failed', e); }
                    }
                }
            };

            // Track every peer connection (one per viewer) so presets reach late joiners too.
            const PC = window.RTCPeerConnection;
            if (PC && PC.prototype.setLocalDescription) {
                const originalSetLocalDescription = PC.prototype.setLocalDescription;
                PC.prototype.setLocalDescription = function(...args) {
                    const pc = this;
                    if (!peerConnections.has(pc)) {
                        peerConnections.add(pc);
                        pc.addEventListener('connectionstatechange', () => {
                            if (pc.connectionState === 'connected') applyToPeerConnection(pc);
                            else if (pc.connectionState === 'closed') peerConnections.delete(pc);
                        });
                    }
                    const result = originalSetLocalDescription.apply(this, args);
                    Promise.resolve(result).then(() => applyToPeerConnection(pc), () => { });
                    return result;
                };
            }

            // Called by the wrapper when the preset changes: applies live, no reload.
            window.__lt1SetQuality = async (q) => {
                quality = q;
                if (q) qualityTouched = true;
                for (const track of [...videoTracks]) await applyToTrack(track);
                for (const pc of [...peerConnections]) await applyToPeerConnection(pc);
                reportCapture();
            };

            // ---- Capture hook ------------------------------------------------------
            mediaDevices.getDisplayMedia = async function(options = {}) {
                const requested = (options && typeof options === 'object') ? options : {};

                let video = requested.video === undefined ? true : requested.video;
                if (quality && video !== false) {
                    video = { ...(typeof video === 'object' ? video : {}), ...videoConstraints(quality) };
                }

                const merged = {
                    ...requested,
                    video,
                    {{captureOptions}}
                };

                console.debug('[Lightone Stream] getDisplayMedia options:', merged);
                const stream = await original(merged);

                for (const track of stream.getVideoTracks()) {
                    videoTracks.add(track);
                    if (quality) track.contentHint = quality.contentHint;
                    track.addEventListener('ended', () => {
                        videoTracks.delete(track);
                        post({ type: 'captureEnded' });
                    });
                }
                reportCapture();
                return stream;
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

        if (uri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(uri.Host))
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

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type))
                return;

            switch (type.GetString())
            {
                case "capture":
                    var w = root.TryGetProperty("width", out var wp) && wp.ValueKind == System.Text.Json.JsonValueKind.Number ? wp.GetInt32() : 0;
                    var h = root.TryGetProperty("height", out var hp) && hp.ValueKind == System.Text.Json.JsonValueKind.Number ? hp.GetInt32() : 0;
                    var fps = root.TryGetProperty("frameRate", out var fp) && fp.ValueKind == System.Text.Json.JsonValueKind.Number ? fp.GetDouble() : 0;
                    _statusLabel.Text = $"Capturing {w}x{h} @ {fps:0} fps";
                    break;
                case "captureEnded":
                    _statusLabel.Text = "Capture stopped";
                    break;
                case "viewers":
                    var count = root.TryGetProperty("count", out var cp) && cp.ValueKind == System.Text.Json.JsonValueKind.Number ? cp.GetInt32() : 0;
                    var live = root.TryGetProperty("live", out var lp) && lp.ValueKind == System.Text.Json.JsonValueKind.True;
                    _statusLabel.Text = live
                        ? $"LIVE - {count} viewer(s)"
                        : $"Not live - {count} in room";
                    break;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Ignore messages that are not ours.
        }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private void CopyShareLink()
    {
        var currentUrl = _urltext.Text.Trim();
        // Screensy and LT1 Direct keep the room in the fragment (#Room); other sites may use path/query.
        Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri);
        var isDirect = uri is not null && uri.Host.Equals(DirectHost, StringComparison.OrdinalIgnoreCase);
        var isScreensy = isDirect ||
                         (uri is not null && uri.Host.Equals("screensy.marijn.it", StringComparison.OrdinalIgnoreCase));
        if (uri is null || (isScreensy && string.IsNullOrWhiteSpace(uri.Fragment)))
        {
            _statusLabel.Text = "Share link is not ready yet";
            MessageBox.Show(
                "Screensy has not created the room link yet. Wait until the page finishes loading, then try again.",
                "Share link not ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // lt1.example only exists inside this app; friends get the public viewer page instead.
        if (isDirect)
            currentUrl = ViewerBaseUrl + uri!.Fragment;

        try
        {
            Clipboard.SetText(currentUrl);
            _statusLabel.Text = isScreensy
                ? $"Share link copied: {uri.Fragment.TrimStart('#')}"
                : "Share link copied";
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

    // Order must match the items in _qualityMode.
    private enum QualityPreset
    {
        Fluido = 0,
        Nitido = 1,
        Padrao = 2,
        Leve = 3
    }

    private enum AudioMode
    {
        WindowOnly = 0,
        SystemAudio = 1,
        NoAudio = 2
    }
}
