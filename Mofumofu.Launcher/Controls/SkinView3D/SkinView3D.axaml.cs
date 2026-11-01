using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace Mofumofu.Launcher.Controls.SkinView3D;

public enum SkinModel
{
    Auto,
    Classic,
    Slim
}

/// <summary>
/// 原生 Avalonia Minecraft 皮肤查看器。仅依赖 OpenGlControlBase 与 Silk.NET，
/// 不依赖启动器的账号、皮肤缓存或 WebView 服务。
/// </summary>
public partial class SkinView3D : OpenGlControlBase
{
    public static readonly StyledProperty<SkinModel> ModelProperty =
        AvaloniaProperty.Register<SkinView3D, SkinModel>(nameof(Model), SkinModel.Auto);

    public static readonly StyledProperty<SkinAnimation> AnimationProperty =
        AvaloniaProperty.Register<SkinView3D, SkinAnimation>(nameof(Animation), SkinAnimation.Idle);

    public static readonly StyledProperty<bool> ShowInnerLayerProperty =
        AvaloniaProperty.Register<SkinView3D, bool>(nameof(ShowInnerLayer), true);

    public static readonly StyledProperty<bool> ShowOuterLayerProperty =
        AvaloniaProperty.Register<SkinView3D, bool>(nameof(ShowOuterLayer), true);

    public static readonly StyledProperty<bool> ShowCapeProperty =
        AvaloniaProperty.Register<SkinView3D, bool>(nameof(ShowCape), true);

    public static readonly StyledProperty<bool> AutoRotateProperty =
        AvaloniaProperty.Register<SkinView3D, bool>(nameof(AutoRotate));

    public static readonly StyledProperty<double> AutoRotateSpeedProperty =
        AvaloniaProperty.Register<SkinView3D, double>(nameof(AutoRotateSpeed), 0.5);

    public static readonly StyledProperty<double> AnimationSpeedProperty =
        AvaloniaProperty.Register<SkinView3D, double>(nameof(AnimationSpeed), 1.0);

    public static readonly StyledProperty<bool> AnimationPausedProperty =
        AvaloniaProperty.Register<SkinView3D, bool>(nameof(AnimationPaused));

    public static readonly StyledProperty<double> AnimationProgressProperty =
        AvaloniaProperty.Register<SkinView3D, double>(nameof(AnimationProgress), 0.0,
            coerce: (_, value) => Math.Clamp(value, 0, 1));

    public static readonly StyledProperty<double> CameraYawProperty =
        AvaloniaProperty.Register<SkinView3D, double>(nameof(CameraYaw), 30.0);

    public static readonly StyledProperty<double> CameraPitchProperty =
        AvaloniaProperty.Register<SkinView3D, double>(nameof(CameraPitch), 8.0,
            coerce: (_, value) => Math.Clamp(value, -89, 89));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<SkinView3D, double>(nameof(Zoom), 1.0,
            coerce: (_, value) => Math.Clamp(value, 0.35, 4));

    public static readonly StyledProperty<double> FieldOfViewProperty =
        AvaloniaProperty.Register<SkinView3D, double>(nameof(FieldOfView), 50.0,
            coerce: (_, value) => Math.Clamp(value, 20, 100));

    public static readonly StyledProperty<Color> BackgroundProperty =
        AvaloniaProperty.Register<SkinView3D, Color>(nameof(Background), Colors.Transparent);

    public static readonly StyledProperty<bool> EnableCameraControlsProperty =
        AvaloniaProperty.Register<SkinView3D, bool>(nameof(EnableCameraControls), true);

    public static readonly StyledProperty<string?> NameTagProperty =
        AvaloniaProperty.Register<SkinView3D, string?>(nameof(NameTag));

    private readonly Dictionary<(SkinBodyPart Part, SkinLayer Layer), bool> _partVisibility = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _ticker;
    private SkinModel _effectiveModel = SkinModel.Classic;
    private SkinTextureData? _skinData;
    private SkinTextureData? _capeData;
    private SkinTextureData? _nameTagData;
    private double _lastTickSeconds;
    private double _animationTime;
    private bool _updatingProgress;
    private bool _dragging;
    private bool _isAttached;
    private Point _lastPointer;

    public SkinView3D()
    {
        AvaloniaXamlLoader.Load(this);

        // 动画与自动旋转都放在 UI 线程上推进：渲染线程只读时间、写帧缓冲，
        // 绝不回写 Avalonia 属性（跨线程改属性会让属性系统与绑定表打架，甚至会崩进程）。
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        UpdateTicker();
    }

    public SkinModel Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public SkinAnimation Animation { get => GetValue(AnimationProperty); set => SetValue(AnimationProperty, value); }
    public bool ShowInnerLayer { get => GetValue(ShowInnerLayerProperty); set => SetValue(ShowInnerLayerProperty, value); }
    public bool ShowOuterLayer { get => GetValue(ShowOuterLayerProperty); set => SetValue(ShowOuterLayerProperty, value); }
    public bool ShowCape { get => GetValue(ShowCapeProperty); set => SetValue(ShowCapeProperty, value); }
    public bool AutoRotate { get => GetValue(AutoRotateProperty); set => SetValue(AutoRotateProperty, value); }
    public double AutoRotateSpeed { get => GetValue(AutoRotateSpeedProperty); set => SetValue(AutoRotateSpeedProperty, value); }
    public double AnimationSpeed { get => GetValue(AnimationSpeedProperty); set => SetValue(AnimationSpeedProperty, value); }
    public bool AnimationPaused { get => GetValue(AnimationPausedProperty); set => SetValue(AnimationPausedProperty, value); }
    public double AnimationProgress { get => GetValue(AnimationProgressProperty); set => SetValue(AnimationProgressProperty, value); }
    public double CameraYaw { get => GetValue(CameraYawProperty); set => SetValue(CameraYawProperty, value); }
    public double CameraPitch { get => GetValue(CameraPitchProperty); set => SetValue(CameraPitchProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double FieldOfView { get => GetValue(FieldOfViewProperty); set => SetValue(FieldOfViewProperty, value); }
    public Color Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public bool EnableCameraControls { get => GetValue(EnableCameraControlsProperty); set => SetValue(EnableCameraControlsProperty, value); }
    public string? NameTag { get => GetValue(NameTagProperty); set => SetValue(NameTagProperty, value); }

    public SkinModel ResolvedModel => _effectiveModel;
    public bool HasSkin => _skinData is not null;
    public bool HasCape => _capeData is not null;

    public event EventHandler? SkinLoaded;
    public event EventHandler? CapeLoaded;

    public async Task LoadSkinAsync(
        string source, SkinModel? model = null, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await SkinTextureLoader.ReadBytesAsync(source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LoadSkin(bytes, model);
    }

    public void LoadSkin(byte[] png, SkinModel? model = null)
    {
        SkinTextureData decoded = SkinTextureLoader.DecodeSkin(png, out _);
        _skinData = decoded;
        if (model.HasValue) Model = model.Value;
        _effectiveModel = ResolveModel(Model, decoded);
        _skinTextureDirty = true;
        _meshesDirty = true;
        RequestNextFrameRendering();
        SkinLoaded?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadCapeAsync(string source, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await SkinTextureLoader.ReadBytesAsync(source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LoadCape(bytes);
    }

    public void LoadCape(byte[] png)
    {
        _capeData = SkinTextureLoader.DecodeCape(png);
        _capeTextureDirty = true;
        RequestNextFrameRendering();
        CapeLoaded?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCape()
    {
        _capeData = null;
        _capeTextureDirty = true;
        RequestNextFrameRendering();
    }

    public void ClearSkin()
    {
        _skinData = null;
        _effectiveModel = SkinModel.Classic;
        _skinTextureDirty = true;
        _meshesDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>清空账号相关贴图，避免空账号或加载失败时继续显示上一账号。</summary>
    public void Clear()
    {
        ClearSkin();
        _capeData = null;
        _nameTagData = null;
        _capeTextureDirty = true;
        _nameTagTextureDirty = true;
        SetCurrentValue(NameTagProperty, null);
    }

    public void SetAnimation(SkinAnimation animation, bool resetProgress = true)
    {
        if (resetProgress)
        {
            _animationTime = 0;
            _renderTime = 0;
            SetCurrentValue(AnimationProgressProperty, 0.0);
        }
        SetCurrentValue(AnimationProperty, animation);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double delta = _lastTickSeconds <= 0 ? 0 : Math.Min(now - _lastTickSeconds, 0.1);
        _lastTickSeconds = now;

        bool animating = !AnimationPaused && Animation != SkinAnimation.None;
        if (animating)
        {
            _animationTime += delta * AnimationSpeed;
            _renderTime = (float)_animationTime;

            double duration = SkinAnimationPlayer.Duration(Animation);
            _updatingProgress = true;
            SetCurrentValue(AnimationProgressProperty, _animationTime % duration / duration);
            _updatingProgress = false;
        }

        if (AutoRotate && !_dragging)
            SetCurrentValue(CameraYawProperty, CameraYaw + delta * AutoRotateSpeed * 24);

        RequestNextFrameRendering();
        UpdateTicker();
    }

    private void UpdateTicker()
    {
        bool shouldRun = _isAttached && ((!AnimationPaused && Animation != SkinAnimation.None) || AutoRotate);
        if (shouldRun && !_ticker.IsEnabled)
        {
            _lastTickSeconds = 0;
            _ticker.Start();
        }
        else if (!shouldRun && _ticker.IsEnabled)
        {
            _ticker.Stop();
            _lastTickSeconds = 0;
        }
    }

    public void SetBodyPartVisible(SkinBodyPart part, SkinLayer layer, bool visible)
    {
        _partVisibility[(part, layer)] = visible;
        _meshesDirty = true;
        RequestNextFrameRendering();
    }

    public bool IsBodyPartVisible(SkinBodyPart part, SkinLayer layer) =>
        IsPartVisible(part, layer);

    public void ResetCamera()
    {
        SetCurrentValue(CameraYawProperty, 30.0);
        SetCurrentValue(CameraPitchProperty, 8.0);
        SetCurrentValue(ZoomProperty, 1.0);
        SetCurrentValue(FieldOfViewProperty, 50.0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateTicker();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 离开可视树就停表，避免隐藏页面继续以 60fps 空转。
        _isAttached = false;
        _ticker.Stop();
        _lastTickSeconds = 0;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModelProperty)
        {
            _effectiveModel = ResolveModel(Model, _skinData);
            _meshesDirty = true;
        }
        else if (change.Property == ShowInnerLayerProperty ||
                 change.Property == ShowOuterLayerProperty ||
                 change.Property == ShowCapeProperty)
        {
            _meshesDirty = true;
        }
        else if (change.Property == AnimationProperty)
        {
            _animationTime = 0;
            _renderTime = 0;
            _updatingProgress = true;
            SetCurrentValue(AnimationProgressProperty, 0.0);
            _updatingProgress = false;
            UpdateTicker();
        }
        else if (change.Property == AnimationPausedProperty || change.Property == AutoRotateProperty)
        {
            UpdateTicker();
        }
        else if (change.Property == AnimationProgressProperty && !_updatingProgress)
        {
            _animationTime = AnimationProgress * SkinAnimationPlayer.Duration(Animation);
            _renderTime = (float)_animationTime;
        }
        else if (change.Property == NameTagProperty)
        {
            RebuildNameTag();
        }

        RequestNextFrameRendering();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // GL 内容由 compositor 绘制；补上透明绘制区域，使整个预览区域参与指针命中测试。
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!EnableCameraControls || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _dragging = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        Point current = e.GetPosition(this);
        Vector delta = current - _lastPointer;
        _lastPointer = current;
        SetCurrentValue(CameraYawProperty, CameraYaw - delta.X * 0.5);
        SetCurrentValue(CameraPitchProperty, CameraPitch + delta.Y * 0.5);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!EnableCameraControls) return;
        SetCurrentValue(ZoomProperty, Zoom * Math.Exp(e.Delta.Y * 0.12));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!EnableCameraControls || e.Handled) return;

        switch (e.Key)
        {
            case Key.Left:
                SetCurrentValue(CameraYawProperty, CameraYaw - 8);
                break;
            case Key.Right:
                SetCurrentValue(CameraYawProperty, CameraYaw + 8);
                break;
            case Key.Up:
                SetCurrentValue(CameraPitchProperty, CameraPitch - 6);
                break;
            case Key.Down:
                SetCurrentValue(CameraPitchProperty, CameraPitch + 6);
                break;
            case Key.Add:
            case Key.OemPlus:
                SetCurrentValue(ZoomProperty, Zoom * 1.1);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                SetCurrentValue(ZoomProperty, Zoom / 1.1);
                break;
            case Key.Home:
                ResetCamera();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private bool IsPartVisible(SkinBodyPart part, SkinLayer layer)
    {
        if (part == SkinBodyPart.Cape && (layer != SkinLayer.Base || !ShowCape)) return false;
        if (layer == SkinLayer.Base && !ShowInnerLayer) return false;
        if (layer == SkinLayer.Overlay && !ShowOuterLayer) return false;
        return !_partVisibility.TryGetValue((part, layer), out bool visible) || visible;
    }

    private static SkinModel ResolveModel(SkinModel requested, SkinTextureData? skin)
    {
        if (requested != SkinModel.Auto) return requested;
        return skin is not null && SkinTextureLoader.DetectSlim(skin)
            ? SkinModel.Slim
            : SkinModel.Classic;
    }

    private void RebuildNameTag()
    {
        _nameTagData = null;
        _nameTagTextureDirty = true;
        if (string.IsNullOrWhiteSpace(NameTag)) return;

        var text = new FormattedText(
            NameTag.Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface.Default, 30, Brushes.White);
        int width = Math.Clamp((int)Math.Ceiling(text.WidthIncludingTrailingWhitespace + 28), 64, 1024);
        int height = Math.Max(48, (int)Math.Ceiling(text.Height + 16));

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(150, 12, 12, 16)),
                new Rect(0, 0, width, height));
            context.DrawText(text, new Point(14, (height - text.Height) / 2));
        }

        _nameTagData = SkinTextureLoader.DecodeNameTag(bitmap);
    }
}
