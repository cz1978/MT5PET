using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using TradePet.Core.Domain;

namespace TradePet.App.Controls;

public sealed class PetSpriteControl : System.Windows.Controls.UserControl, IDisposable
{
    public static readonly DependencyProperty ActivityProperty = DependencyProperty.Register(
        nameof(Activity),
        typeof(PetActivity),
        typeof(PetSpriteControl),
        new PropertyMetadata(PetActivity.Idle, OnActivityChanged));

    public static readonly DependencyProperty AtlasPathProperty = DependencyProperty.Register(
        nameof(AtlasPath),
        typeof(string),
        typeof(PetSpriteControl),
        new PropertyMetadata(string.Empty, OnAtlasPathChanged));

    public static readonly DependencyProperty PlaybackRateProperty = DependencyProperty.Register(
        nameof(PlaybackRate),
        typeof(double),
        typeof(PetSpriteControl),
        new FrameworkPropertyMetadata(1.0, OnPlaybackRateChanged, CoercePlaybackRate));

    private readonly SKElement _surface;
    private readonly DispatcherTimer _timer;
    private SKBitmap? _atlas;
    private int _frameIndex;
    private bool _disposed;
    private bool _animationPaused;

    public PetSpriteControl()
    {
        _surface = new SKElement();
        _surface.PaintSurface += PaintSurface;
        Content = _surface;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => AdvanceFrame();
        Loaded += (_, _) =>
        {
            LoadAtlas();
            ResetAnimation();
            UpdateAnimationTimer();
        };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateAnimationTimer();
    }

    public PetActivity Activity
    {
        get => (PetActivity)GetValue(ActivityProperty);
        set => SetValue(ActivityProperty, value);
    }

    public string AtlasPath
    {
        get => (string)GetValue(AtlasPathProperty);
        set => SetValue(AtlasPathProperty, value);
    }

    public double PlaybackRate
    {
        get => (double)GetValue(PlaybackRateProperty);
        set => SetValue(PlaybackRateProperty, value);
    }

    public void SetAnimationPaused(bool paused)
    {
        _animationPaused = paused;
        UpdateAnimationTimer();
    }

    private void UpdateAnimationTimer() =>
        _timer.IsEnabled = !_disposed && !_animationPaused && IsLoaded && IsVisible;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _surface.PaintSurface -= PaintSurface;
        _atlas?.Dispose();
    }

    private static void OnActivityChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        ((PetSpriteControl)dependencyObject).ResetAnimation();
    }

    private static void OnAtlasPathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        ((PetSpriteControl)dependencyObject).LoadAtlas();
    }

    private static void OnPlaybackRateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        ((PetSpriteControl)dependencyObject).ResetAnimation();
    }

    private static object CoercePlaybackRate(DependencyObject dependencyObject, object value)
    {
        var rate = (double)value;
        return double.IsFinite(rate) ? Math.Clamp(rate, 0.25, 2.0) : 1.0;
    }

    private void LoadAtlas()
    {
        if (_disposed || string.IsNullOrWhiteSpace(AtlasPath))
        {
            return;
        }

        var path = Path.IsPathRooted(AtlasPath) ? AtlasPath : Path.Combine(AppContext.BaseDirectory, AtlasPath);
        if (!File.Exists(path))
        {
            return;
        }

        var replacement = SKBitmap.Decode(path);
        if (replacement is null)
        {
            return;
        }

        _atlas?.Dispose();
        _atlas = replacement;
        _surface.InvalidateVisual();
    }

    private void ResetAnimation()
    {
        _frameIndex = 0;
        _timer.Interval = GetFrameDuration(GetAnimation(Activity).Durations[0]);
        _surface.InvalidateVisual();
    }

    private void AdvanceFrame()
    {
        var animation = GetAnimation(Activity);
        _frameIndex = (_frameIndex + 1) % animation.Durations.Length;
        _timer.Interval = GetFrameDuration(animation.Durations[_frameIndex]);
        _surface.InvalidateVisual();
    }

    private TimeSpan GetFrameDuration(int sourceMilliseconds) =>
        TimeSpan.FromMilliseconds(sourceMilliseconds / PlaybackRate);

    private void PaintSurface(object? sender, SKPaintSurfaceEventArgs eventArgs)
    {
        var canvas = eventArgs.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_atlas is null)
        {
            return;
        }

        var animation = GetAnimation(Activity);
        var source = new SKRect(
            _frameIndex * 192,
            animation.Row * 208,
            (_frameIndex + 1) * 192,
            (animation.Row + 1) * 208);
        var scale = Math.Min(eventArgs.Info.Width / 192f, eventArgs.Info.Height / 208f);
        var width = 192f * scale;
        var height = 208f * scale;
        var destination = new SKRect(
            (eventArgs.Info.Width - width) / 2f,
            eventArgs.Info.Height - height,
            (eventArgs.Info.Width + width) / 2f,
            eventArgs.Info.Height);
        using var paint = new SKPaint
        {
            IsAntialias = false,
            FilterQuality = SKFilterQuality.None,
        };
        canvas.DrawBitmap(_atlas, source, destination, paint);
    }

    private static AnimationDefinition GetAnimation(PetActivity activity) => activity switch
    {
        PetActivity.Idle => new(0, [280, 110, 110, 140, 140, 320]),
        PetActivity.RunningRight => new(1, [120, 120, 120, 120, 120, 120, 120, 220]),
        PetActivity.RunningLeft => new(2, [120, 120, 120, 120, 120, 120, 120, 220]),
        PetActivity.Waving => new(3, [140, 140, 140, 280]),
        PetActivity.Jumping => new(4, [140, 140, 140, 140, 280]),
        PetActivity.Failed => new(5, [140, 140, 140, 140, 140, 140, 140, 240]),
        PetActivity.Waiting => new(6, [150, 150, 150, 150, 150, 260]),
        PetActivity.Running => new(7, [120, 120, 120, 120, 120, 220]),
        PetActivity.Review => new(8, [150, 150, 150, 150, 150, 280]),
        _ => new(0, [280, 110, 110, 140, 140, 320]),
    };

    private sealed record AnimationDefinition(int Row, int[] Durations);
}
