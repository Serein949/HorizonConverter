using Android.Content;
using Android.Graphics;
using Android.Provider;
using Android.Views;

namespace AeroflyConv.Android;

/// <summary>
/// Animated background: soft colour blobs drifting on slow Lissajous paths (added together so
/// they read as light), a wide light beam sweeping across the screen, a couple of drifting
/// glints and a vignette that keeps the foreground readable.
/// Everything is drawn with Canvas primitives — no bitmaps to ship, no third-party libraries.
/// </summary>
public sealed class AuroraView : View
{
    private sealed class Blob
    {
        public float SpeedX;
        public float SpeedY;
        public float PhaseX;
        public float PhaseY;
        public float Radius;      // fraction of the shorter edge
        public float HueOffset;
        public float Alpha;
    }

    private static readonly Color BaseColor = Color.Argb(255, 5, 7, 12);

    // Hue travel: one full turn of the wheel every 48 s, so the palette keeps moving
    // through cyan -> violet -> magenta -> amber -> emerald instead of sitting still.
    private const float HueCycleSeconds = 48f;

    private readonly Blob[] _blobs =
    [
        new() { SpeedX = 0.13f, SpeedY = 0.09f, PhaseX = 0.0f,  PhaseY = 1.1f,  Radius = 0.58f, HueOffset = 0f,   Alpha = 0.32f },
        new() { SpeedX = 0.09f, SpeedY = 0.15f, PhaseX = 2.1f,  PhaseY = 0.4f,  Radius = 0.50f, HueOffset = 52f,  Alpha = 0.27f },
        new() { SpeedX = 0.17f, SpeedY = 0.11f, PhaseX = 4.0f,  PhaseY = 2.7f,  Radius = 0.44f, HueOffset = 118f, Alpha = 0.25f },
        new() { SpeedX = 0.07f, SpeedY = 0.19f, PhaseX = 1.3f,  PhaseY = 3.9f,  Radius = 0.52f, HueOffset = 186f, Alpha = 0.23f },
        new() { SpeedX = 0.21f, SpeedY = 0.13f, PhaseX = 3.4f,  PhaseY = 5.2f,  Radius = 0.34f, HueOffset = 248f, Alpha = 0.20f },
        new() { SpeedX = 0.11f, SpeedY = 0.23f, PhaseX = 5.6f,  PhaseY = 1.8f,  Radius = 0.30f, HueOffset = 310f, Alpha = 0.18f },
    ];

    private readonly Paint _blobPaint = new(PaintFlags.FilterBitmap) { AntiAlias = true };
    private readonly Paint _beamPaint = new(PaintFlags.FilterBitmap) { AntiAlias = true };
    private readonly Paint _vignettePaint = new(PaintFlags.FilterBitmap) { AntiAlias = true };
    private readonly Paint _glintPaint = new(PaintFlags.FilterBitmap) { AntiAlias = true };
    private Shader?[] _shaders = [];
    private int[] _shaderColours = [];
    private readonly float[] _hsv = new float[3];

    private Shader? _beamShader;
    private Shader? _vignetteShader;
    private readonly RectF _rect = new();
    private long _startNanos;
    private float _elapsed;
    private bool _visible = true;

    public AuroraView(Context context) : base(context) => Init();

    public AuroraView(Context context, global::Android.Util.IAttributeSet? attrs) : base(context, attrs) => Init();

    public AuroraView(IntPtr handle, global::Android.Runtime.JniHandleOwnership ownership)
        : base(handle, ownership) => Init();

    private void Init()
    {
        _shaders = new Shader?[_blobs.Length];
        _shaderColours = new int[_blobs.Length];
        Array.Fill(_shaderColours, -1);
        SetLayerType(LayerType.Hardware, null);
        _blobPaint.SetXfermode(new PorterDuffXfermode(PorterDuff.Mode.Add));
        _glintPaint.SetXfermode(new PorterDuffXfermode(PorterDuff.Mode.Add));
        _startNanos = Java.Lang.JavaSystem.NanoTime();
        _elapsed = 0f;
    }

    /// <summary>Reduced motion (accessibility / battery saver): freeze the animation.</summary>
    public bool ReducedMotion
    {
        get
        {
            try
            {
                float scale = global::Android.Provider.Settings.Global.GetFloat(
                    Context!.ContentResolver, global::Android.Provider.Settings.Global.AnimatorDurationScale, 1f);
                return scale <= 0f;
            }
            catch
            {
                return true;
            }
        }
    }

    public void Pause() => _visible = false;

    public void Resume()
    {
        if (_visible)
        {
            return;
        }

        _visible = true;
        _startNanos = Java.Lang.JavaSystem.NanoTime() - (long)(_elapsed * 1e9);
        Invalidate();
    }

    protected override void OnWindowVisibilityChanged(ViewStates visibility)
    {
        base.OnWindowVisibilityChanged(visibility);
        _visible = visibility == ViewStates.Visible;
        if (_visible)
        {
            Invalidate();
        }
    }

    protected override void OnDraw(Canvas canvas)
    {
        int w = Width;
        int h = Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        canvas.DrawColor(BaseColor);

        float shorter = Math.Min(w, h);
        float cx = w * 0.5f;
        float cy = h * 0.5f;

        // additive blobs
        canvas.Save();
        for (int i = 0; i < _blobs.Length; i++)
        {
            Blob b = _blobs[i];
            float x = cx + w * 0.42f * MathF.Sin(_elapsed * b.SpeedX * MathF.Tau + b.PhaseX);
            float y = cy + h * 0.40f * MathF.Sin(_elapsed * b.SpeedY * MathF.Tau + b.PhaseY);
            float radius = shorter * b.Radius * (0.88f + 0.12f * MathF.Sin(_elapsed * 0.18f * MathF.Tau + b.PhaseX));
            int colour = BlobColour(i, b);

            if (_shaders[i] is null || _shaderColours[i] != colour)
            {
                float r = Math.Max(1f, shorter * b.Radius);
                _shaders[i] = new RadialGradient(0, 0, r, colour, colour & 0x00FFFFFF, Shader.TileMode.Clamp!);
                _shaderColours[i] = colour;
                _blobPaint.SetShader(_shaders[i]);
            }
            else
            {
                _blobPaint.SetShader(_shaders[i]);
            }

            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(radius / Math.Max(1f, shorter * b.Radius), radius / Math.Max(1f, shorter * b.Radius));
            canvas.DrawCircle(0, 0, Math.Max(1f, shorter * b.Radius), _blobPaint);
            canvas.Restore();
        }
        canvas.Restore();

        // sweeping light beam
        float angle = _elapsed * 6.0f; // degrees per second -> a lazy 60 s turn
        float beamWidth = shorter * 0.55f;
        if (_beamShader is null)
        {
            _beamShader = new LinearGradient(
                -beamWidth, 0, beamWidth, 0,
                [Color.Argb(0, 255, 255, 255), Color.Argb(26, 214, 245, 255), Color.Argb(0, 255, 255, 255)],
                [0f, 0.5f, 1f],
                Shader.TileMode.Clamp!);
            _beamPaint.SetShader(_beamShader);
        }

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Rotate(angle);
        float span = MathF.Sqrt(w * w + h * h);
        canvas.DrawRect(-span * 0.5f, -span * 0.5f, span * 0.5f, span * 0.5f, _beamPaint);
        canvas.Restore();

        // two small drifting glints
        for (int i = 0; i < 2; i++)
        {
            float x = w * (0.5f + 0.38f * MathF.Sin(_elapsed * (0.07f + i * 0.02f) * MathF.Tau + i * 2.4f));
            float y = h * (0.5f + 0.34f * MathF.Cos(_elapsed * (0.05f + i * 0.03f) * MathF.Tau + i * 1.7f));
            _glintPaint.SetShader(new RadialGradient(x, y, shorter * 0.06f,
                Color.Argb(70, 255, 255, 255), Color.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
            canvas.DrawCircle(x, y, shorter * 0.06f, _glintPaint);
        }

        // vignette keeps the cards and text readable over the bright spots
        if (_vignetteShader is null)
        {
            _vignetteShader = new RadialGradient(cx, cy * 1.02f, Math.Max(w, h) * 0.78f,
                [Color.Argb(0, 3, 5, 9), Color.Argb(40, 3, 5, 9), Color.Argb(150, 3, 5, 9)],
                [0f, 0.58f, 1f],
                Shader.TileMode.Clamp!);
            _vignettePaint.SetShader(_vignetteShader);
        }

        canvas.DrawRect(0, 0, w, h, _vignettePaint);

        if (!_visible)
        {
            return;
        }

        if (ReducedMotion)
        {
            // one static frame is enough when the system asks for no animation
            _visible = false;
            return;
        }

        _elapsed = (Java.Lang.JavaSystem.NanoTime() - _startNanos) / 1e9f;
        PostInvalidateOnAnimation();
    }

    private int BlobColour(int index, Blob blob)
    {
        float hue = (_elapsed * 360f / HueCycleSeconds + blob.HueOffset) % 360f;
        _hsv[0] = hue;
        _hsv[1] = 0.80f;
        _hsv[2] = 0.72f;
        return Color.HSVToColor((int)Math.Clamp(blob.Alpha * 255f, 0, 255), _hsv);
    }
}
