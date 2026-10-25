using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HeatingMold.App;

public enum ErrorLevel
{
    Warning,
    Error
}

/// <summary>
/// 실행 중 발생한 오류를 모달 창으로 표시한다.
/// 창 안의 본문은 읽기 전용 TextBox라서 마우스로 드래그해 선택하고 Ctrl+C로 복사할 수 있다.
/// </summary>
public static class ErrorReporter
{
    private static readonly object Gate = new object();
    private static bool _showing;
    private static string _lastSignature;
    private static DateTime _lastShownUtc;

    public static void ShowError(string context, string message)
        => Show(context, message, ErrorLevel.Error);

    public static void ShowError(string context, Exception ex)
        => Show(context, Format(ex), ErrorLevel.Error);

    public static void ShowWarning(string context, string message)
        => Show(context, message, ErrorLevel.Warning);

    public static void ShowWarning(string context, Exception ex)
        => Show(context, Format(ex), ErrorLevel.Warning);

    private static string Format(Exception ex)
    {
        if (ex == null) return "(예외 정보 없음)";
        try { return ex.ToString(); }
        catch (Exception) { return ex.GetType().FullName + ": " + ex.Message; }
    }

    private static void Show(string context, string detail, ErrorLevel level)
    {
        try
        {
            var app = Application.Current;
            if (app == null)
            {
                MessageBox.Show(context + Environment.NewLine + Environment.NewLine + detail,
                    "HeatingMold 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!app.Dispatcher.CheckAccess())
            {
                // 백그라운드 스레드에서 온 오류도 UI 스레드에서 표시한다.
                app.Dispatcher.Invoke(new Action(() => ShowOnUiThread(context, detail, level)));
                return;
            }

            ShowOnUiThread(context, detail, level);
        }
        catch (Exception)
        {
            try
            {
                MessageBox.Show(context + Environment.NewLine + Environment.NewLine + detail,
                    "HeatingMold 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception) { }
        }
    }

    private static void ShowOnUiThread(string context, string detail, ErrorLevel level)
    {
        string signature = level + "|" + context + "|" + detail;
        lock (Gate)
        {
            // 표시 중 재진입하거나 같은 오류가 연속 발생하면 창이 무한히 쌓이지 않게 한다.
            if (_showing) return;
            if (signature == _lastSignature && (DateTime.UtcNow - _lastShownUtc).TotalSeconds < 2) return;
            _showing = true;
            _lastSignature = signature;
            _lastShownUtc = DateTime.UtcNow;
        }

        try
        {
            var window = BuildDialog(context, detail, level);
            var owner = ResolveOwner();
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
        }
        finally
        {
            lock (Gate) { _showing = false; }
        }
    }

    private static Window ResolveOwner()
    {
        var app = Application.Current;
        if (app == null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
    }

    private static Window BuildDialog(string context, string detail, ErrorLevel level)
    {
        bool isError = level == ErrorLevel.Error;

        var headline = new TextBlock
        {
            Text = context ?? "",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xB0, 0x1C, 0x1C))
                : new SolidColorBrush(Color.FromRgb(0xA8, 0x54, 0x00)),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var detailBox = new TextBox
        {
            Text = detail ?? "",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0))
        };

        var hint = new TextBlock
        {
            Text = "위 내용은 마우스로 드래그해 선택하고 Ctrl+C로 복사할 수 있습니다.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var copyButton = new Button { Content = "복사", Width = 88, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var closeButton = new Button { Content = "닫기", Width = 88, Height = 28, IsDefault = true, IsCancel = true };
        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        buttonBar.Children.Add(copyButton);
        buttonBar.Children.Add(closeButton);

        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(headline, Dock.Top);
        panel.Children.Add(headline);

        Window window = null;
        copyButton.Click += (sender, args) =>
        {
            for (int i = 0; i < 3; i++)
            {
                try { Clipboard.SetText(detailBox.Text ?? ""); return; }
                catch (Exception) { System.Threading.Thread.Sleep(50); }
            }
        };
        closeButton.Click += (sender, args) => window.Close();

        DockPanel.SetDock(buttonBar, Dock.Bottom);
        panel.Children.Add(buttonBar);
        DockPanel.SetDock(hint, Dock.Bottom);
        panel.Children.Add(hint);
        panel.Children.Add(detailBox);

        window = new Window
        {
            Title = (isError ? "오류" : "경고") + " — HeatingMold",
            Width = 860,
            Height = 520,
            MinWidth = 480,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = panel
        };
        window.Loaded += (sender, args) => detailBox.Focus();
        return window;
    }
}
