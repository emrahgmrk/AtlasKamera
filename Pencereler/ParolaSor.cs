using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AtlasKamera.Pencereler;

/// <summary>Aktarma parolası soran küçük iletişim kutusu (kod ile kurulur).</summary>
public static class ParolaSor
{
    public static string? Sor(Window sahip, string baslik, string aciklama)
    {
        var pencere = new Window
        {
            Title = baslik,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = sahip,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x1B)),
        };

        var yigin = new StackPanel { Margin = new Thickness(16) };
        yigin.Children.Add(new TextBlock
        {
            Text = aciklama,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var kutu = new PasswordBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x30, 0x41)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
            Padding = new Thickness(6, 5, 6, 5),
            FontSize = 13,
        };
        yigin.Children.Add(kutu);

        var dugmeler = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var tamam = new Button { Content = "Tamam", Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
        var vazgec = new Button
        {
            Content = "Vazgeç",
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        dugmeler.Children.Add(tamam);
        dugmeler.Children.Add(vazgec);
        yigin.Children.Add(dugmeler);

        pencere.Content = yigin;

        string? sonuc = null;
        tamam.Click += (_, _) => { sonuc = kutu.Password; pencere.DialogResult = true; };
        pencere.Loaded += (_, _) => kutu.Focus();
        pencere.ShowDialog();
        return string.IsNullOrEmpty(sonuc) ? null : sonuc;
    }
}
