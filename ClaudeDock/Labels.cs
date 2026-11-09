using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeDock;

/// <summary>User-chosen labels per Claude session id, stored in ~/.claude-dock/labels.json.</summary>
internal static class LabelStore
{
    public const int MaxLength = 45;
    private static readonly string FilePath = Path.Combine(DataDir.Path, "labels.json");
    private static readonly object Gate = new();
    private static Dictionary<string, string> _labels = Load();
    private static DateTime _loadedStamp = Stamp();

    public static event Action? Changed;

    public static string? Get(string sessionId)
    {
        lock (Gate)
        {
            ReloadIfChanged();
            return _labels.TryGetValue(sessionId, out var l) ? l : null;
        }
    }

    // Another process (or a hand edit) may have changed the file.
    private static void ReloadIfChanged()
    {
        var stamp = Stamp();
        if (stamp != _loadedStamp)
        {
            _labels = Load();
            _loadedStamp = stamp;
        }
    }

    private static DateTime Stamp() => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    public static void Set(string sessionId, string? label)
    {
        label = label?.Trim();
        if (label is { Length: > MaxLength })
        {
            label = label[..MaxLength];
        }

        lock (Gate)
        {
            ReloadIfChanged();
            if (string.IsNullOrEmpty(label))
            {
                _labels.Remove(sessionId);
            }
            else
            {
                _labels[sessionId] = label;
            }

            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_labels, LabelJsonContext.Default.DictionaryStringString));
                _loadedStamp = Stamp();
            }
            catch (Exception ex)
            {
                Log.Write($"Save labels: {ex.Message}");
            }
        }

        Changed?.Invoke();
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LabelJsonContext.Default.DictionaryStringString) ?? [];
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Load labels: {ex.Message}");
        }

        return [];
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LabelJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>
/// The Dock has no double-click event, so a second click on the same session within the
/// system double-click time is treated as a double-click.
/// </summary>
internal static class DoubleClickDetector
{
    private static readonly ConcurrentDictionary<string, long> LastClick = new();

    public static bool IsDoubleClick(string key)
    {
        var now = Environment.TickCount64;
        var limit = Math.Max(NativeWindow.DoubleClickTimeMs(), 500) + 150;
        if (LastClick.TryGetValue(key, out var last) && now - last <= limit)
        {
            LastClick.TryRemove(key, out _);
            return true;
        }

        LastClick[key] = now;
        return false;
    }
}

/// <summary>Small always-on-top WPF box to edit a session label.</summary>
internal static class RenameDialog
{
    private static int _open;
    private static Thread? _thread;

    /// <summary>For the --rename diagnostics mode.</summary>
    public static void WaitClosed() => _thread?.Join();

    public static void Show(ClaudeSession session)
    {
        Log.Write($"RenameDialog.Show {session.Key}");
        if (Interlocked.Exchange(ref _open, 1) == 1)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                ShowCore(session);
            }
            catch (Exception ex)
            {
                Log.Write($"Rename dialog: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _open, 0);
            }
        });
        _thread = thread;
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static void ShowCore(ClaudeSession session)
    {
        var current = LabelStore.Get(session.SessionId) ?? string.Empty;
        var box = new TextBox
        {
            Text = current,
            MaxLength = LabelStore.MaxLength,
            FontSize = 14,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var counter = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) };
        void UpdateCounter() => counter.Text = $"{box.Text.Length}/{LabelStore.MaxLength} · Enter = save · Esc = cancel · empty = clear";
        box.TextChanged += (_, _) => UpdateCounter();
        UpdateCounter();

        var window = new Window
        {
            Title = "Claude Dock: label this session",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = $"Label for: {session.Title}", TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{session.Source} · {session.Cwd}", Foreground = System.Windows.Media.Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis },
                    box,
                    counter,
                },
            },
        };

        var save = false;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                save = true;
                window.Close();
            }
            else if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };
        window.Loaded += (_, _) =>
        {
            window.Activate();
            box.Focus();
            box.SelectAll();
        };

        window.ShowDialog();
        if (save)
        {
            LabelStore.Set(session.SessionId, box.Text);
        }
    }
}
