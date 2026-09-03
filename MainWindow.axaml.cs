using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BitchatWin.Protocol;
using BitchatWin.Services;
using BitchatWin.ViewModels;

namespace BitchatWin;

public partial class MainWindow : Window
{
    private const int MaxMessages = 1000;
    private const int MaxLogLines = 300;

    private readonly MainViewModel _vm = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GeohashChannelService _channel;

    private readonly LocalRelayServer _relayServer = new();
    private readonly LanBeacon _beacon = new();
    private DispatcherTimer? _relayStatusTimer;

    private TrayIcon? _tray;
    private double _restoreWidth = 1000;
    private double _restoreHeight = 640;
    private bool _shuttingDown;

    /// <summary>
    /// False until the persisted appearance has been pushed onto the controls.
    /// Setting a checkbox raises its changed event, and acting on those events
    /// mid-initialisation hid the window before it was ever shown.
    /// </summary>
    private bool _windowReady;



    public MainWindow()
    {
        InitializeComponent();

        _channel = new GeohashChannelService(new IdentityStore());
        _channel.MessageReceived += OnMessageReceived;
        _channel.ParticipantsChanged += OnParticipantsChanged;
        _channel.RelayStatusChanged += OnRelayStatusChanged;
        _channel.Log += AppendLog;
        _channel.SystemNotice += AppendSystem;

        // Set before the window is shown: Avalonia creates the native window
        // with this style, so it never flashes in the taskbar first.
        ShowInTaskbar = !_settings.HideFromTaskbar;

        _vm.Nickname = _settings.Nickname;
        _vm.GeohashInput = _settings.LastGeohash;
        _vm.WindowTitle = _settings.WindowTitle;
        _channel.Nickname = _settings.Nickname;
        DataContext = _vm;

        JoinButton.Click += OnJoinClicked;
        LeaveButton.Click += OnLeaveClicked;
        DeriveButton.Click += OnDeriveClicked;
        SendButton.Click += OnSendClicked;
        DraftBox.KeyDown += OnDraftKeyDown;
        NicknameBox.LostFocus += (_, _) => ApplyNickname();
        TitleBox.LostFocus += (_, _) => ApplyWindowTitle();

        CompactButton.Click += (_, _) => SetCompact(true);
        ExpandButton.Click += (_, _) => SetCompact(false);
        HideButton.Click += (_, _) => HideToTray();
        HideButtonCompact.Click += (_, _) => HideToTray();

        HostRelayCheck.IsCheckedChanged += (_, _) => ApplyLocalRelayHosting();
        LocalOnlyCheck.IsCheckedChanged += (_, _) => ApplyLocalRelay();
        RelayBox.LostFocus += (_, _) => ApplyLocalRelay();

        _relayServer.Log += AppendLog;
        _beacon.Log += AppendLog;
        _beacon.RelayDiscovered += url => Dispatcher.UIThread.Post(() => OnRelayDiscovered(url));
        _beacon.StartListening();

        TopmostCheck.IsCheckedChanged += (_, _) => ApplyTopmost();
        TaskbarCheck.IsCheckedChanged += (_, _) => ApplyTaskbarVisibility();
        OpacitySlider.ValueChanged += (_, _) => ApplyOpacity();

        // Tunnelling means the discretion keys work even while a text box has
        // focus — a panic key that only fires when nothing is focused is useless.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        Closing += OnClosing;
        Opened += (_, _) => ApplyDiscretionSettings();

        SetupTrayIcon();

        AppendSystem("client windows non ufficiale — canali geohash su Nostr, compatibili con bitchat iOS/Android.");
        AppendSystem($"directory relay: {GeoRelayDirectory.Shared.Count} relay noti.");
        AppendSystem("Esc nasconde la finestra (torna dall'icona nella tray) · Ctrl+M compatta · Ctrl+T sempre davanti.");

        // Best-effort refresh so the relay set tracks upstream without a rebuild.
        _ = Task.Run(async () =>
        {
            bool updated = await GeoRelayDirectory.Shared.RefreshAsync().ConfigureAwait(false);
            if (updated) AppendLog($"directory  aggiornata da upstream ({GeoRelayDirectory.Shared.Count} relay)");
        });

        AutoJoinFromCommandLine();
    }

    // MARK: - Discretion

    /// <summary>Pushes the persisted appearance settings onto the live window.</summary>
    private void ApplyDiscretionSettings()
    {
        TopmostCheck.IsChecked = _settings.AlwaysOnTop;
        TaskbarCheck.IsChecked = _settings.HideFromTaskbar;
        OpacitySlider.Value = Math.Clamp(_settings.Opacity, 0.35, 1.0);

        Topmost = _settings.AlwaysOnTop;
        Opacity = Math.Clamp(_settings.Opacity, 0.35, 1.0);
        ApplyCompactLayout(_settings.CompactMode);

        // No bounce here: the window has only just been shown, and hiding it to
        // refresh a style would leave it invisible on startup.
        ApplyExtendedStyle();

        RelayBox.Text = _settings.LocalRelayUrl;
        LocalOnlyCheck.IsChecked = _settings.LocalOnly;
        HostRelayCheck.IsChecked = _settings.HostLocalRelay;

        _windowReady = true;

        // Applied after the guard is lifted so the handlers above act for real.
        if (_settings.HostLocalRelay) ApplyLocalRelayHosting();
        ApplyLocalRelay();

        if (Environment.GetCommandLineArgs().Contains("--hidden")) HideToTray();
    }

    // MARK: - Local network (no internet)

    private void ApplyLocalRelayHosting()
    {
        if (!_windowReady) return;

        _settings.HostLocalRelay = HostRelayCheck.IsChecked ?? false;
        _settings.Save();

        if (_settings.HostLocalRelay) StartHosting();
        else StopHosting();
    }

    private void StartHosting()
    {
        try
        {
            int port = _relayServer.Start();
            _beacon.StartAnnouncing(port);

            // This machine talks to its own relay over loopback; the address other
            // machines need is shown alongside, and broadcast for them anyway.
            RelayBox.Text = $"ws://127.0.0.1:{port}";
            ApplyLocalRelay();

            _relayStatusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _relayStatusTimer.Tick -= OnRelayStatusTick;
            _relayStatusTimer.Tick += OnRelayStatusTick;
            _relayStatusTimer.Start();
            OnRelayStatusTick(this, EventArgs.Empty);

            string addresses = string.Join(", ", LocalRelayServer.LocalAddresses().Select(a => $"ws://{a}:{port}"));
            AppendSystem($"relay locale avviato. Sugli altri PC: {(addresses.Length == 0 ? "nessun indirizzo di rete" : addresses)}");
            AppendSystem("sulla stessa rete l'indirizzo viene trovato da solo — basta spuntare \"solo rete locale\".");
        }
        catch (Exception ex)
        {
            AppendSystem("relay locale non avviato: " + ex.Message);
            HostRelayCheck.IsChecked = false;
        }
    }

    private void StopHosting()
    {
        _relayStatusTimer?.Stop();
        _beacon.StopAnnouncing();
        _ = _relayServer.StopAsync();
        LocalRelayStatus.Text = string.Empty;
    }

    private void OnRelayStatusTick(object? sender, EventArgs e)
    {
        LocalRelayStatus.Text = _relayServer.IsRunning
            ? $"porta {_relayServer.Port} · {_relayServer.ClientCount} connessi · {_relayServer.StoredEventCount} eventi"
            : string.Empty;
    }

    private void OnRelayDiscovered(string url)
    {
        // Never override a relay the user typed, and never point a host at itself
        // through the network stack when loopback already works.
        if (_relayServer.IsRunning) return;
        if (!string.IsNullOrWhiteSpace(RelayBox.Text)) return;

        RelayBox.Text = url;
        ApplyLocalRelay();
        AppendSystem($"trovato un relay locale su {url} — spunta \"solo rete locale\" per usarlo senza internet.");
    }

    private void ApplyLocalRelay()
    {
        if (!_windowReady) return;

        _settings.LocalRelayUrl = RelayBox.Text?.Trim() ?? string.Empty;
        _settings.LocalOnly = LocalOnlyCheck.IsChecked ?? false;
        _settings.Save();

        _channel.SetLocalRelay(_settings.LocalRelayUrl, _settings.LocalOnly);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideToTray();
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        switch (e.Key)
        {
            case Key.M:
                e.Handled = true;
                SetCompact(!_settings.CompactMode);
                break;
            case Key.T:
                e.Handled = true;
                TopmostCheck.IsChecked = !(TopmostCheck.IsChecked ?? false);
                break;
        }
    }

    /// <summary>
    /// Hides the window without closing it: the channel stays joined and
    /// messages keep arriving, so nothing is lost while it is out of sight.
    /// </summary>
    private void HideToTray()
    {
        Hide();
        if (_tray is not null) _tray.IsVisible = true;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible) HideToTray();
        else ShowFromTray();
    }

    private void SetCompact(bool compact)
    {
        _settings.CompactMode = compact;
        _settings.Save();
        ApplyCompactLayout(compact);
    }

    /// <summary>
    /// Compact mode strips the window down to the conversation and the input
    /// box: no app name, no controls, no roster, no log — a small panel that
    /// does not read as a chat client at a glance.
    /// </summary>
    private void ApplyCompactLayout(bool compact)
    {
        if (compact && WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        if (compact)
        {
            // Remember the roomy size so expanding restores what the user had.
            if (HeaderBar.IsVisible)
            {
                _restoreWidth = Width;
                _restoreHeight = Height;
            }
        }

        HeaderBar.IsVisible = !compact;
        ControlsPanel.IsVisible = !compact;
        LogExpander.IsVisible = !compact;
        PeopleColumn.IsVisible = !compact;
        CompactStrip.IsVisible = compact;

        MessagesGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 10);
        MessagesGrid.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 230);
        RootGrid.Margin = new Thickness(compact ? 6 : 12);

        if (compact)
        {
            Width = 360;
            Height = 300;
        }
        else
        {
            Width = _restoreWidth;
            Height = _restoreHeight;
        }
    }

    private void ApplyTopmost()
    {
        _settings.AlwaysOnTop = TopmostCheck.IsChecked ?? false;
        _settings.Save();
        Topmost = _settings.AlwaysOnTop;
    }

    private void ApplyOpacity()
    {
        double value = Math.Clamp(OpacitySlider.Value, 0.35, 1.0);
        _settings.Opacity = value;
        _settings.Save();
        Opacity = value;
    }

    private void ApplyWindowTitle()
    {
        string title = string.IsNullOrWhiteSpace(_vm.WindowTitle) ? "Note" : _vm.WindowTitle.Trim();
        _vm.WindowTitle = title;
        _settings.WindowTitle = title;
        _settings.Save();
        if (_tray is not null) _tray.ToolTipText = title;
    }

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    /// <summary>
    /// Removes the window from the taskbar and, via WS_EX_TOOLWINDOW, from the
    /// Alt+Tab list too. ShowInTaskbar alone leaves it in Alt+Tab, which is the
    /// place someone is most likely to notice it.
    /// </summary>
    private void ApplyTaskbarVisibility()
    {
        if (!_windowReady) return;

        _settings.HideFromTaskbar = TaskbarCheck.IsChecked ?? false;
        _settings.Save();

        ShowInTaskbar = !_settings.HideFromTaskbar;

        if (!ApplyExtendedStyle()) return;

        // Alt+Tab only re-reads the style when the window is shown again, so
        // bounce it — but re-show on a later dispatcher pass, never inline,
        // or the window can be left hidden.
        if (!IsVisible) return;
        Hide();
        Dispatcher.UIThread.Post(Show, DispatcherPriority.Background);
    }

    /// <summary>
    /// Writes WS_EX_TOOLWINDOW / WS_EX_APPWINDOW to match the setting.
    /// Returns true when the style actually changed.
    /// </summary>
    private bool ApplyExtendedStyle()
    {
        if (!OperatingSystem.IsWindows()) return false;

        IntPtr handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;

        try
        {
            long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            long updated = _settings.HideFromTaskbar
                ? (style | WsExToolWindow) & ~WsExAppWindow
                : (style & ~WsExToolWindow) | WsExAppWindow;

            if (style == updated) return false;

            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(updated));
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("stile finestra non applicato: " + ex.Message);
            return false;
        }
    }

    private void SetupTrayIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://BitchatWin/Assets/tray.png"));
            var icon = new WindowIcon(new Bitmap(stream));

            var showItem = new NativeMenuItem("Mostra / nascondi");
            showItem.Click += (_, _) => ToggleWindowVisibility();

            var compactItem = new NativeMenuItem("Modalità compatta");
            compactItem.Click += (_, _) => { ShowFromTray(); SetCompact(!_settings.CompactMode); };

            var exitItem = new NativeMenuItem("Esci");
            exitItem.Click += (_, _) =>
            {
                _shuttingDown = true;
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            };

            var menu = new NativeMenu();
            menu.Add(showItem);
            menu.Add(compactItem);
            menu.Add(exitItem);

            _tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = _settings.WindowTitle,
                IsVisible = true,
                Menu = menu
            };
            _tray.Clicked += (_, _) => ToggleWindowVisibility();

            if (Application.Current is not null)
            {
                TrayIcon.SetIcons(Application.Current, new TrayIcons { _tray });
            }
        }
        catch (Exception ex)
        {
            AppendLog("icona tray non disponibile: " + ex.Message);
        }
    }

    // MARK: - Channel

    private void ApplyNickname()
    {
        string nickname = string.IsNullOrWhiteSpace(_vm.Nickname) ? "anon" : _vm.Nickname.Trim();
        _vm.Nickname = nickname;
        _channel.Nickname = nickname;
        _settings.Nickname = nickname;
        _settings.Save();
        UpdateIdentityLine();
    }

    /// <summary>Honours <c>--join &lt;geohash&gt;</c> so a channel can be opened straight from a shortcut.</summary>
    private void AutoJoinFromCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--join");
        if (index < 0 || index + 1 >= args.Length) return;

        _vm.GeohashInput = args[index + 1];
        Dispatcher.UIThread.Post(() => OnJoinClicked(this, new RoutedEventArgs()), DispatcherPriority.Background);
    }

    private async void OnJoinClicked(object? sender, RoutedEventArgs e)
    {
        ApplyNickname();
        string geohash = (_vm.GeohashInput ?? string.Empty).Trim().ToLowerInvariant();

        if (!Geohash.IsValid(geohash))
        {
            AppendSystem($"'{geohash}' non è un geohash valido (1-12 caratteri base32).");
            return;
        }

        JoinButton.IsEnabled = false;
        try
        {
            await _channel.JoinAsync(geohash);

            _vm.IsJoined = true;
            _vm.ChannelTitle = "#" + geohash;
            _settings.LastGeohash = geohash;
            _settings.Save();
            UpdateIdentityLine();
            DraftBox.Focus();
        }
        catch (Exception ex)
        {
            AppendSystem("ingresso fallito: " + ex.Message);
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }

    private async void OnLeaveClicked(object? sender, RoutedEventArgs e)
    {
        await _channel.LeaveAsync();
        _vm.IsJoined = false;
        _vm.ChannelTitle = "nessun canale";
        _vm.People.Clear();
        _vm.PeopleHeader = "presenti (0)";
        UpdateIdentityLine();
        AppendSystem("uscito dal canale.");
    }

    private void OnDeriveClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryParseCoordinate(_vm.Latitude, out double lat) ||
            !TryParseCoordinate(_vm.Longitude, out double lon))
        {
            AppendSystem("coordinate non valide.");
            return;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            AppendSystem("coordinate fuori range.");
            return;
        }

        int precision = 5;
        if (LevelBox.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            int.TryParse(tag, out int parsed))
        {
            precision = parsed;
        }

        _vm.GeohashInput = Geohash.Encode(lat, lon, precision);
        AppendSystem(string.Format(CultureInfo.InvariantCulture,
            "geohash per ({0:F4}, {1:F4}) a precisione {2}: {3}", lat, lon, precision, _vm.GeohashInput));
    }

    /// <summary>
    /// Accepts both decimal separators: an Italian keyboard types "45,4642" and
    /// a copied coordinate is usually "45.4642". Neither should be an error.
    /// </summary>
    private static bool TryParseCoordinate(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string normalised = text.Trim().Replace(',', '.');
        return double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private async void OnSendClicked(object? sender, RoutedEventArgs e) => await SendDraftAsync();

    private async void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        await SendDraftAsync();
    }

    private async Task SendDraftAsync()
    {
        string text = (_vm.Draft ?? string.Empty).Trim();
        if (text.Length == 0 || !_vm.IsJoined) return;

        _vm.Draft = string.Empty;
        try
        {
            await _channel.SendMessageAsync(text);
        }
        catch (Exception ex)
        {
            AppendSystem("invio fallito: " + ex.Message);
        }
    }

    private void OnMessageReceived(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        _vm.Messages.Add(new MessageRow(message));
        while (_vm.Messages.Count > MaxMessages) _vm.Messages.RemoveAt(0);
        MessageScroller.ScrollToEnd();
    });

    private void OnParticipantsChanged() => Dispatcher.UIThread.Post(() =>
    {
        var people = _channel.Participants;

        _vm.People.Clear();
        foreach (var person in people) _vm.People.Add(person);
        _vm.PeopleHeader = $"presenti ({people.Count})";
    });

    private void OnRelayStatusChanged(int connected, int total) => Dispatcher.UIThread.Post(() =>
    {
        _vm.RelayStatus = $"relay {connected}/{total}";
        _vm.RelayBrush = new SolidColorBrush(connected switch
        {
            0 => Color.Parse("#E06C6C"),
            _ when connected < total => Color.Parse("#E0C26C"),
            _ => Color.Parse("#7DF9A6")
        });
    });

    private void UpdateIdentityLine() => Dispatcher.UIThread.Post(() =>
    {
        _vm.IdentityLine = _channel.CurrentIdentity is null
            ? "identità: —"
            : "identità: " + _channel.MyDisplayName;
    });

    private void AppendSystem(string text) => Dispatcher.UIThread.Post(() =>
    {
        _vm.Messages.Add(MessageRow.System(text));
        MessageScroller.ScrollToEnd();
    });

    private void AppendLog(string line) => Dispatcher.UIThread.Post(() =>
    {
        _vm.LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_vm.LogLines.Count > MaxLogLines) _vm.LogLines.RemoveAt(0);
    });

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Closing the window hides it instead of quitting: the tray icon is the
        // way out, so a stray Alt+F4 does not silently drop the channel.
        if (!_shuttingDown && _tray is not null)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_tray is not null) _tray.IsVisible = false;
        _relayStatusTimer?.Stop();
        await _beacon.DisposeAsync();
        await _relayServer.DisposeAsync();
        await _channel.DisposeAsync();
    }
}
