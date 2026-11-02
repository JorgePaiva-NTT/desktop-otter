using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace OtterDesktop;

internal enum CompanionPropKind
{
    Ball,
    Snack
}

internal sealed class CompanionPropWindow : Window
{
    public CompanionPropWindow(CompanionPropKind kind)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        IsHitTestVisible = false;

        Content = kind == CompanionPropKind.Ball ? CreateBall() : CreateSnack();
        (Width, Height) = kind == CompanionPropKind.Ball ? (42, 48) : (64, 38);
    }

    public bool ExcludeFromCapture { get; set; } = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPrivacy.Apply(this, ExcludeFromCapture);
    }

    private static UIElement CreateBall()
    {
        var canvas = new Canvas();
        canvas.Children.Add(new Ellipse
        {
            Width = 32,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            Effect = new BlurEffect { Radius = 4 },
            Margin = new Thickness(5, 34, 0, 0)
        });
        canvas.Children.Add(new Ellipse
        {
            Width = 34,
            Height = 34,
            Fill = new RadialGradientBrush(
                Color.FromRgb(255, 193, 64),
                Color.FromRgb(220, 67, 51)),
            Stroke = new SolidColorBrush(Color.FromRgb(128, 39, 31)),
            StrokeThickness = 2,
            Margin = new Thickness(4, 2, 0, 0)
        });
        canvas.Children.Add(new Ellipse
        {
            Width = 9,
            Height = 7,
            Fill = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            Margin = new Thickness(11, 8, 0, 0)
        });
        return canvas;
    }

    private static UIElement CreateSnack()
    {
        var canvas = new Canvas();
        canvas.Children.Add(new Ellipse
        {
            Width = 46,
            Height = 20,
            Fill = new LinearGradientBrush(
                Color.FromRgb(103, 192, 211),
                Color.FromRgb(38, 118, 150),
                90),
            Stroke = new SolidColorBrush(Color.FromRgb(30, 83, 103)),
            StrokeThickness = 1.5,
            Margin = new Thickness(5, 8, 0, 0)
        });
        canvas.Children.Add(new Polygon
        {
            Points = new PointCollection([new Point(48, 18), new Point(61, 7), new Point(61, 29)]),
            Fill = new SolidColorBrush(Color.FromRgb(58, 145, 171)),
            Stroke = new SolidColorBrush(Color.FromRgb(30, 83, 103)),
            StrokeThickness = 1.5
        });
        canvas.Children.Add(new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            Margin = new Thickness(13, 13, 0, 0)
        });
        return canvas;
    }
}
