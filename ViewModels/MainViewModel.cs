using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using BitchatWin.Services;

namespace BitchatWin.ViewModels;

/// <summary>A rendered chat line.</summary>
public sealed class MessageRow
{
    public MessageRow(ChatMessage message)
    {
        Time = message.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Name = message.DisplayName;
        Content = message.Content;
        IsMine = message.IsMine;
        NameBrush = IsMine ? Palette.Own : Palette.ForPubkey(message.SenderPubkey);
    }

    private MessageRow(string time, string name, string content, IBrush brush)
    {
        Time = time;
        Name = name;
        Content = content;
        NameBrush = brush;
        IsMine = false;
    }

    public string Time { get; }
    public string Name { get; }
    public string Content { get; }
    public bool IsMine { get; }
    public IBrush NameBrush { get; }

    /// <summary>A local notice (joins, errors) rendered in the message flow.</summary>
    public static MessageRow System(string text) => new(
        DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
        "*",
        text,
        Palette.SystemNotice);
}

/// <summary>Colours for the terminal-styled UI.</summary>
public static class Palette
{
    public static readonly IBrush Own = new SolidColorBrush(Color.Parse("#7DF9A6"));
    public static readonly IBrush SystemNotice = new SolidColorBrush(Color.Parse("#6B7F6B"));

    /// <summary>
    /// A stable colour per author, so the same person keeps the same colour
    /// across a session without any name registry.
    /// </summary>
    public static IBrush ForPubkey(string pubkeyHex)
    {
        int hash = 17;
        foreach (char c in pubkeyHex) hash = unchecked(hash * 31 + c);
        double hue = Math.Abs(hash) % 360;
        return new SolidColorBrush(FromHsl(hue, 0.55, 0.68));
    }

    private static Color FromHsl(double hueDegrees, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double h = hueDegrees / 60.0;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = lightness - c / 2;

        (double r, double g, double b) = h switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MessageRow> Messages { get; } = new();
    public ObservableCollection<Participant> People { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private string _nickname = "anon";
    public string Nickname
    {
        get => _nickname;
        set => Set(ref _nickname, value);
    }

    private string _geohashInput = "u0nd";
    public string GeohashInput
    {
        get => _geohashInput;
        set => Set(ref _geohashInput, value);
    }

    private string _latitude = "45.4642";
    public string Latitude
    {
        get => _latitude;
        set => Set(ref _latitude, value);
    }

    private string _longitude = "9.1900";
    public string Longitude
    {
        get => _longitude;
        set => Set(ref _longitude, value);
    }

    private string _channelTitle = "nessun canale";
    public string ChannelTitle
    {
        get => _channelTitle;
        set => Set(ref _channelTitle, value);
    }

    private string _identityLine = "identità: —";
    public string IdentityLine
    {
        get => _identityLine;
        set => Set(ref _identityLine, value);
    }

    private string _relayStatus = "relay 0/0";
    public string RelayStatus
    {
        get => _relayStatus;
        set => Set(ref _relayStatus, value);
    }

    private IBrush _relayBrush = new SolidColorBrush(Color.Parse("#6B7F6B"));
    public IBrush RelayBrush
    {
        get => _relayBrush;
        set => Set(ref _relayBrush, value);
    }

    private string _peopleHeader = "presenti (0)";
    public string PeopleHeader
    {
        get => _peopleHeader;
        set => Set(ref _peopleHeader, value);
    }

    private string _draft = string.Empty;
    public string Draft
    {
        get => _draft;
        set => Set(ref _draft, value);
    }

    private bool _isJoined;
    public bool IsJoined
    {
        get => _isJoined;
        set => Set(ref _isJoined, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
