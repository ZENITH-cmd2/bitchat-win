using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    public MainWindow()
    {
        InitializeComponent();

        _channel = new GeohashChannelService(new IdentityStore());
        _channel.MessageReceived += OnMessageReceived;
        _channel.ParticipantsChanged += OnParticipantsChanged;
        _channel.RelayStatusChanged += OnRelayStatusChanged;
        _channel.Log += AppendLog;
        _channel.SystemNotice += AppendSystem;

        _vm.Nickname = _settings.Nickname;
        _vm.GeohashInput = _settings.LastGeohash;
        _channel.Nickname = _settings.Nickname;
        DataContext = _vm;

        JoinButton.Click += OnJoinClicked;
        LeaveButton.Click += OnLeaveClicked;
        DeriveButton.Click += OnDeriveClicked;
        SendButton.Click += OnSendClicked;
        DraftBox.KeyDown += OnDraftKeyDown;
        NicknameBox.LostFocus += (_, _) => ApplyNickname();
        Closing += OnClosing;

        AppendSystem("client windows non ufficiale — canali geohash su Nostr, compatibili con bitchat iOS/Android.");
        AppendSystem($"directory relay: {GeoRelayDirectory.Shared.Count} relay noti.");

        // Best-effort refresh so the relay set tracks upstream without a rebuild.
        _ = Task.Run(async () =>
        {
            bool updated = await GeoRelayDirectory.Shared.RefreshAsync().ConfigureAwait(false);
            if (updated) AppendLog($"directory  aggiornata da upstream ({GeoRelayDirectory.Shared.Count} relay)");
        });

        AutoJoinFromCommandLine();
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

    private void ApplyNickname()
    {
        string nickname = string.IsNullOrWhiteSpace(_vm.Nickname) ? "anon" : _vm.Nickname.Trim();
        _vm.Nickname = nickname;
        _channel.Nickname = nickname;
        _settings.Nickname = nickname;
        _settings.Save();
        UpdateIdentityLine();
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
        await _channel.DisposeAsync();
    }
}
