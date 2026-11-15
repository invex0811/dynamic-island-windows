using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The Notifications tab: every message the island has shown and you haven't got to yet. A message leaves
/// the list when you click it (its program opens), switch to that program yourself, press its
/// "remove" button or clear the list. Kept in memory only, like the clipboard history.
/// </summary>
public partial class MainWindow
{
    private sealed record InboxItem(string App, string AppId, string Title, string Body, ImageSource? Logo, DateTime At);

    private readonly List<InboxItem> _inbox = [];   // newest first
    private const int InboxMax = 100;
    private IntPtr _inboxFront;

    private bool InboxPageOpen => _mode == Mode.Expanded && TabNotify.IsChecked == true;

    private void AddToInbox(NotificationMirror.Message m, ImageSource? logo)
    {
        _inbox.Insert(0, new InboxItem(m.App, m.AppId, m.Title, m.Body, logo, DateTime.Now));
        if (_inbox.Count > InboxMax) _inbox.RemoveRange(InboxMax, _inbox.Count - InboxMax);
        InboxChanged();
    }

    private void RemoveFromInbox(Predicate<InboxItem> which)
    {
        if (_inbox.RemoveAll(which) > 0) InboxChanged();
    }

    private void InboxChanged()
    {
        _unread = _inbox.Count;   // the badge on the folded pill
        if (InboxPageOpen) RenderInbox();
    }

    /// <summary>You switched to a program yourself: its messages count as read.</summary>
    private void InboxSeenForeground(IntPtr front)
    {
        if (front == _inboxFront) return;
        _inboxFront = front;
        if (_inbox.Count == 0 || front == IntPtr.Zero || front == _hwnd || OwnsWindow(front)) return;
        // (asked once per sender, not per message)
        var theirs = new Dictionary<(string, string), bool>();
        RemoveFromInbox(i => theirs.TryGetValue((i.AppId, i.App), out bool yes) ? yes
                             : theirs[(i.AppId, i.App)] = AppWindows.Belongs(front, i.AppId, i.App));
    }

    /// <summary>Open the program a message came from (see <see cref="AppWindows.Open"/>); its messages leave the list.</summary>
    private void OpenFromInbox(string app, string appId)
    {
        RemoveFromInbox(i => i.App == app && i.AppId == appId);
        AppWindows.Open(appId, app);
    }

    private void InboxClear_Click(object sender, RoutedEventArgs e) => RemoveFromInbox(_ => true);

    private void RenderInbox()
    {
        InboxList.Children.Clear();
        InboxClear.Visibility = _inbox.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var today = DateTime.Today;
        foreach (var item in _inbox)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
            if (item.Logo != null) badge.Background = new ImageBrush(item.Logo) { Stretch = Stretch.UniformToFill };
            else
            {
                badge.SetResourceReference(Border.BackgroundProperty, "ChipBg");
                badge.Child = new TextBlock
                {
                    Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            row.Children.Add(badge);

            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (item.Body.Length > 0)
                body.Children.Add(new TextBlock { Text = item.Body, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 52 });
            string when = item.At.Date == today ? item.At.ToString("HH:mm") : item.At.ToString("d MMM, HH:mm", L.Culture);
            body.Children.Add(new TextBlock { Text = $"{item.App} · {when}", Style = (Style)FindResource("Muted"), FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            var it = item;
            var del = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 28, Height = 28, FontSize = 11, ToolTip = L.T("Убрать"), VerticalAlignment = VerticalAlignment.Top };
            del.Click += (_, _) => RemoveFromInbox(i => ReferenceEquals(i, it));
            Grid.SetColumn(del, 2);
            row.Children.Add(del);

            var tile = new Border { Style = (Style)FindResource("Tile"), Padding = new Thickness(10, 8, 6, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, Child = row };
            tile.MouseLeftButtonUp += (_, _) =>
            {
                if (_justDragged) return;
                OpenFromInbox(it.App, it.AppId);
                _pinnedOpen = false;
                SetMode(Mode.Compact);
            };
            InboxList.Children.Add(tile);
        }
        if (_inbox.Count == 0)
            InboxList.Children.Add(new TextBlock { Text = L.T("Непрочитанных уведомлений нет"), Style = (Style)FindResource("Muted"), Margin = new Thickness(4, 4, 0, 4) });
        Remeasure();
    }
}
