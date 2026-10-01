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
    private const string DirectHost = "lt1.stream";
    private const string DirectUrl = "https://" + DirectHost + "/";
    private const string ViewerBaseUrl = "https://carekovisk.github.io/lt1_stream/";

    // Hosts the embedded browser may navigate to; anything else opens in the default browser.
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        DirectHost,
        "screensy.marijn.it"
    };

    private readonly WebView2 _webView = new();
    private readonly ComboBox _urltext = new(); // service selector (order must match Service)
    private readonly ComboBox _audioMode = new();
    private readonly ComboBox _qualityMode = new();
    private readonly ToolStripStatusLabel _runtimeLabel = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _turnLabel = new(); // LT1 Direct relay availability
    private readonly Button _reloadButton = new();
    private readonly Button _copyShareLinkButton = new();
    private readonly Button _openBrowserButton = new();

    // Custom service: address row shown only while "Custom" is selected.
    private readonly TableLayoutPanel _customBar = new();
    private readonly TextBox _customUrlText = new();
    private readonly Button _customGoButton = new();
    private string? _customUrl; // last address confirmed with Enter/Go

    // Service currently loaded, and the last page (room) of each one, so re-selecting the same
    // service is a no-op and switching back reopens the same room instead of creating a new one.
    private Service _currentService = Service.Lightone;
    private readonly Dictionary<Service, string> _lastServiceUrls = new();

    private string? _injectedScriptId;
    private bool _initialized;

    public MainForm()
    {
        Text = "Lightone Stream - 1.0.2";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(900, 620);

        BuildUi();
        Shown += async (_, _) => await InitializeWebViewAsync();
    }

    private void BuildUi()
    {
        // Service selector: fixed list, not editable; picking one navigates to it.
        _urltext.DropDownStyle = ComboBoxStyle.DropDownList;
        _urltext.Width = 150;
        _urltext.Font = new Font(_urltext.Font, FontStyle.Bold);
        _urltext.Items.AddRange(new object[] { "Lightone", "Screensy", "Custom" });
        _urltext.SelectedIndex = (int)Service.Lightone;
        _urltext.SelectionChangeCommitted += (_, _) => OnServiceChanged();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 7, 8, 5),
            AutoSize = false
        };

        var serviceLabel = new Label
        {
            Text = "Service:",
            AutoSize = true,
            Margin = new Padding(0, 7, 5, 0)
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
            "Window/Game only",
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
            "Max (60 fps)",
            "High",
            "Default",
            "Low"
        });
        _qualityMode.SelectedIndex = (int)QualityPreset.Default;
        _qualityMode.SelectedIndexChanged += async (_, _) =>
        {
            if (_initialized)
                await ApplyQualityAsync();
        };

        var boldFont = new Font(_reloadButton.Font, FontStyle.Bold);

        _reloadButton.Text = "Reload";
        _reloadButton.AutoSize = true;
        _reloadButton.Font = boldFont;
        _reloadButton.Margin = new Padding(8, 3, 3, 3);
        _reloadButton.Click += (_, _) => _webView.Reload();
        _reloadButton.Visible = false;

        //_copyShareLinkButton.ImageAlign = ContentAlignment.MiddleLeft;
        //_copyShareLinkButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        //_copyShareLinkButton.Image = SystemIcons.Information.ToBitmap();
        //_copyShareLinkButton.Image = new Bitmap(_copyShareLinkButton.Image, new Size(22, 22));

        _copyShareLinkButton.Text = "Copy Share Link";
        _copyShareLinkButton.AutoSize = true;
        _copyShareLinkButton.Font = boldFont;
        //_copyShareLinkButton.Margin = new Padding(8, 3, 3, 3);
        _copyShareLinkButton.Click += (_, _) => CopyShareLink();

        _openBrowserButton.Text = "Open in Edge";
        _openBrowserButton.AutoSize = true;
        _openBrowserButton.Click += (_, _) => OpenExternal(ScreensyUrl);
        _openBrowserButton.Visible = false; // Hide this button for now, as it may not be necessary for most users.

        toolbar.Controls.Add(serviceLabel);
        toolbar.Controls.Add(_urltext);
        toolbar.Controls.Add(_reloadButton);
        toolbar.Controls.Add(audioLabel);
        toolbar.Controls.Add(_audioMode);
        toolbar.Controls.Add(qualityLabel);
        toolbar.Controls.Add(_qualityMode);
        toolbar.Controls.Add(_copyShareLinkButton);
        toolbar.Controls.Add(_openBrowserButton);

        // Bottom status bar: runtime version on the left, current status next to it.
        _runtimeLabel.Text = "WebView2: checking...";
        _runtimeLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _statusLabel.Text = "Starting...";
        var statusBar = new StatusStrip { SizingGrip = false };
        statusBar.Items.Add(_runtimeLabel);
        statusBar.Items.Add(_statusLabel);
        _turnLabel.Spring = true;
        _turnLabel.TextAlign = ContentAlignment.MiddleRight;
        statusBar.Items.Add(_turnLabel);

        // Custom address row (below the toolbar): label + address box (stretches) + Go.
        _customBar.Dock = DockStyle.Top;
        _customBar.Height = 36;
        _customBar.ColumnCount = 3;
        _customBar.RowCount = 1;
        _customBar.Padding = new Padding(8, 0, 8, 6);
        _customBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _customBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _customBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _customBar.Visible = false;

        var customLabel = new Label
        {
            Text = "Address:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 5, 0)
        };

        _customUrlText.Dock = DockStyle.Fill;
        _customUrlText.Font = new Font(_customUrlText.Font, FontStyle.Bold);
        _customUrlText.PlaceholderText = "https://example.com";
        _customUrlText.Margin = new Padding(0, 3, 5, 0);
        _customUrlText.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.Handled = true;
            e.SuppressKeyPress = true; // no "ding"
            NavigateToCustom();
        };

        _customGoButton.Text = "Go";
        _customGoButton.AutoSize = true;
        _customGoButton.Font = boldFont;
        _customGoButton.Margin = new Padding(0, 1, 0, 0);
        _customGoButton.Click += (_, _) => NavigateToCustom();

        _customBar.Controls.Add(customLabel, 0, 0);
        _customBar.Controls.Add(_customUrlText, 1, 0);
        _customBar.Controls.Add(_customGoButton, 2, 0);

        _webView.Dock = DockStyle.Fill;

        // Fill must be added first so the docked bars take their space before it;
        // among Top bars, the one added last sits on top (toolbar above the custom row).
        Controls.Add(_webView);
        Controls.Add(_customBar);
        Controls.Add(toolbar);
        Controls.Add(statusBar);
    }

    private string? ServiceUrl(Service service) => service switch
    {
        Service.Screensy => ScreensyUrl,
        Service.Custom => _customUrl,
        _ => DirectUrl
    };

    private void OnServiceChanged()
    {
        var service = (Service)_urltext.SelectedIndex;
        _customBar.Visible = service == Service.Custom;

        // The combo box also fires when the already-selected item is picked again; reloading
        // then would drop a live stream for everyone watching.
        if (service == _currentService)
        {
            if (service == Service.Custom)
            {
                _customUrlText.Focus();
                _customUrlText.SelectAll();
            }
            return;
        }

        RememberCurrentPage();
        _currentService = service;

        if (service == Service.Custom)
        {
            _customUrlText.Focus();
            _customUrlText.SelectAll();
            // Reopen the last custom site, if any; otherwise wait for Enter/Go.
            if (_customUrl is not null)
                _webView.CoreWebView2?.Navigate(_customUrl);
            else
                _statusLabel.Text = "Type an address and press Enter or Go";
            return;
        }

        _webView.CoreWebView2?.Navigate(
            _lastServiceUrls.TryGetValue(service, out var lastUrl) ? lastUrl : ServiceUrl(service)!);
    }

    // Lightone and Screensy keep the room in the URL fragment (#Room), so the current address
    // is enough to come back to the same room. Custom already remembers its own address.
    private void RememberCurrentPage()
    {
        if (_currentService == Service.Custom)
            return;

        var expectedHost = new Uri(ServiceUrl(_currentService)!).Host;
        if (Uri.TryCreate(_webView.CoreWebView2?.Source, UriKind.Absolute, out var uri) &&
            uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(uri.Fragment))
            _lastServiceUrls[_currentService] = uri.AbsoluteUri;
    }

    private void NavigateToCustom()
    {
        var core = _webView.CoreWebView2;
        if (core is null)
            return;

        var text = _customUrlText.Text.Trim();
        if (text.Length == 0)
            return;

        if (!text.Contains("://"))
            text = "https://" + text;

        // https only: screen capture (getDisplayMedia) and the audio/quality hook need it.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _statusLabel.Text = "Invalid address (https only)";
            return;
        }

        // The user explicitly chose this site, so let it load inside the app.
        _allowedHosts.Add(uri.Host);
        _customUrl = uri.AbsoluteUri;
        _customUrlText.Text = _customUrl;
        core.Navigate(_customUrl);
    }

    private bool IsAllowedHost(string host) =>
        _allowedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                               host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private async Task InitializeWebViewAsync()
    {
        try
        {
            _statusLabel.Text = "Initializing WebView2...";
            // The app is a broadcaster: while minimized, Chromium would treat the page as hidden and
            // throttle its timers (down to once a minute after ~5 min), so viewers joining later
            // time out before the page answers them. Keep it running at full speed instead.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = string.Join(' ',
                    "--disable-background-timer-throttling",
                    "--disable-renderer-backgrounding",
                    "--disable-backgrounding-occluded-windows",
                    "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling")
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, null, options);
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            // F12 stays off for normal use; "Lightone-Stream.exe --devtools" enables it for debugging.
            core.Settings.AreDevToolsEnabled = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--devtools", StringComparison.OrdinalIgnoreCase));
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.IsZoomControlEnabled = true;

            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.WebMessageReceived += Core_WebMessageReceived;

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

    // Serves the embedded broadcaster page for https://lt1.stream/ straight from the exe
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

    // null = Default: no capture constraints; senders get the uncapped baseline (see hook).
    private static string QualityJson(QualityPreset preset) => preset switch
    {
        QualityPreset.Max => """{"fps":60,"maxWidth":1920,"maxHeight":1080,"maxBitrate":8000000,"contentHint":"motion","degradation":"maintain-framerate"}""",
        QualityPreset.High => """{"fps":30,"maxWidth":1920,"maxHeight":1080,"maxBitrate":6000000,"contentHint":"detail","degradation":"maintain-resolution"}""",
        QualityPreset.Low => """{"fps":30,"maxWidth":1280,"maxHeight":720,"maxBitrate":1500000,"contentHint":"motion","degradation":"balanced"}""",
        _ => "null"
    };

    private static string BuildInjectionScript(AudioMode mode, QualityPreset quality)
    {
        var qualityJson = QualityJson(quality);

        // "audio: requestedAudio" keeps the page's own audio constraints (e.g. Screensy and
        // LT1 Direct disable noise suppression / echo cancellation) instead of replacing them.
        var captureOptions = mode switch
        {
            AudioMode.SystemAudio => "audio: requestedAudio, windowAudio: 'system', systemAudio: 'include'",
            AudioMode.NoAudio => "audio: false, windowAudio: 'exclude', systemAudio: 'exclude'",
            _ => "audio: requestedAudio, windowAudio: 'window', systemAudio: 'exclude'"
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
            const videoTracks = new Set();

            // Sender baseline (same values Screensy sets itself). Without an explicit
            // maxBitrate, Chromium's bandwidth estimate never probes upward and screen
            // shares stay stuck around 600 kbps / 320x180 even on a fast network.
            const BASELINE_VIDEO_MAX_BITRATE = 100000000; // 100 Mbps = effectively uncapped
            const AUDIO_MAX_BITRATE = 960000;             // Opus default is only ~32 kbps
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
                const isVideo = sender.track.kind === 'video';
                for (const enc of p.encodings) {
                    if (!isVideo) {
                        enc.maxBitrate = AUDIO_MAX_BITRATE;
                    } else if (quality) {
                        enc.maxBitrate = quality.maxBitrate;
                        enc.maxFramerate = quality.fps;
                    } else {
                        enc.maxBitrate = BASELINE_VIDEO_MAX_BITRATE;
                        delete enc.maxFramerate;
                    }
                }
                if (isVideo && withDegradation && quality) p.degradationPreference = quality.degradation;
                else delete p.degradationPreference;
                await sender.setParameters(p);
            };

            const applyToPeerConnection = async (pc) => {
                if (pc.connectionState === 'closed') { peerConnections.delete(pc); return; }
                for (const sender of pc.getSenders()) {
                    if (!sender.track) continue;
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

                const requestedAudio = (requested.audio && typeof requested.audio === 'object') ? requested.audio : true;

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
        _turnLabel.Text = ""; // only LT1 Direct reports it

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
                case "turn":
                    var servers = root.TryGetProperty("servers", out var sp) && sp.ValueKind == System.Text.Json.JsonValueKind.Number ? sp.GetInt32() : 0;
                    _turnLabel.Text = servers > 0 ? "TURN: OK" : "TURN: unavailable (direct only)";
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
        var service = (Service)_urltext.SelectedIndex;
        var serviceUrl = ServiceUrl(service); // null for Custom before any Enter/Go
        var expectedHost = serviceUrl is null ? null : new Uri(serviceUrl).Host;

        // Read the address from the page, but only trust it once the page actually belongs to
        // the selected service: right after switching, the previous service is still loaded.
        var currentUrl = _webView.CoreWebView2?.Source ?? "";
        Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri);

        // Lightone and Screensy keep the room in the fragment (#Room); a custom site may
        // use any URL shape, so it only has to be on the chosen host.
        if (uri is null || expectedHost is null ||
            !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            (service != Service.Custom && string.IsNullOrWhiteSpace(uri.Fragment)))
        {
            _statusLabel.Text = "Share link is not ready yet";
            MessageBox.Show(
                $"{_urltext.SelectedItem} has not created the room link yet. Wait until the page finishes loading, then try again.",
                "Share link not ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // lt1.stream only exists inside this app; friends get the public viewer page instead.
        if (service == Service.Lightone)
            currentUrl = ViewerBaseUrl + uri.Fragment;

        try
        {
            Clipboard.SetText(currentUrl);
            _statusLabel.Text = string.IsNullOrWhiteSpace(uri.Fragment)
                ? $"{_urltext.SelectedItem} link copied"
                : $"{_urltext.SelectedItem} link copied: {uri.Fragment.TrimStart('#')}";

            MessageBox.Show(
                $"Link copied!",
                "Lightone Stream",
                MessageBoxButtons.OK,
                MessageBoxIcon.None);

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
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
        SuspendLayout();
        // 
        // MainForm
        // 
        ClientSize = new Size(282, 253);
        Icon = (Icon)resources.GetObject("$this.Icon");
        Name = "MainForm";
        Load += MainForm_Load;
        ResumeLayout(false);

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

    private void MainForm_Load(object sender, EventArgs e)
    {

    }

    // Order must match the items in _urltext.
    private enum Service
    {
        Lightone = 0,
        Screensy = 1,
        Custom = 2
    }

    // Order must match the items in _qualityMode.
    private enum QualityPreset
    {
        Max = 0,
        High = 1,
        Default = 2,
        Low = 3
    }

    private enum AudioMode
    {
        WindowOnly = 0,
        SystemAudio = 1,
        NoAudio = 2
    }
}
