using System;
using System.IO;
using System.Text;
using AeroflyConv.Core.Codecs;
using AeroflyConv.Core.Media;
using Android.App;
using Android.Content;
using Android.Opengl;
using Android.Util;
using Java.Nio;
using Javax.Microedition.Khronos.Egl;
using Javax.Microedition.Khronos.Opengles;
using Path = System.IO.Path;
using GLES20 = Android.Opengl.GLES20;

namespace AeroflyConv.Android;

/// <summary>
/// Decodes an ASTC payload on the device GPU and compares it against the managed
/// decoder. Used to verify that what the encoder writes is something real hardware
/// accepts: software decoders happily accept block combinations that GPUs reject,
/// which shows up as black or garbled blocks inside the game.
/// </summary>
[Activity(
    Label = "GPU ASTC check",
    Exported = true,
    Theme = "@android:style/Theme.NoTitleBar")]
[IntentFilter(new[] { "com.aeroflyconv.GPUASTC" })]
public sealed class GpuAstcActivity : Activity
{
    public const string ExtraBlock = "block";
    public const string ExtraSource = "src";

    private GLSurfaceView? _view;

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        string block = Intent?.GetStringExtra(ExtraBlock) ?? "6x6";
        string source = Intent?.GetStringExtra(ExtraSource) ?? "src.png";

        var renderer = new AstcRenderer(this, block, source);
        _view = new GLSurfaceView(this) { PreserveEGLContextOnPause = true };
        _view.SetEGLContextClientVersion(3);
        _view.SetRenderer(renderer);
        SetContentView(_view);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _view?.OnResume();
    }

    protected override void OnPause()
    {
        _view?.OnPause();
        base.OnPause();
    }

    private sealed class AstcRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
    {
        private const string Tag = "AeroflyGpuAstc";

        private readonly Activity _activity;
        private readonly string _block;
        private readonly string _source;

        private int _program;
        private int _texture;
        private int _quad;
        private int _aPosition;
        private int _aTexCoord;
        private int _uSize;
        private int _uTex;

        private byte[]? _payload;
        private byte[]? _software;
        private int _width;
        private int _height;
        private int _blockWidth;
        private int _blockHeight;
        private bool _done;

        public AstcRenderer(Activity activity, string block, string source)
        {
            _activity = activity;
            _block = block;
            _source = source;
        }

        public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
        {
            GLES20.GlClearColor(1f, 0f, 1f, 1f);
            _program = BuildProgram();
            _aPosition = GLES20.GlGetAttribLocation(_program, "aPos");
            _aTexCoord = GLES20.GlGetAttribLocation(_program, "aUv");
            _uSize = GLES20.GlGetUniformLocation(_program, "uSize");
            _uTex = GLES20.GlGetUniformLocation(_program, "uTex");

            float[] quad =
            {
                0f, 0f, 0f, 0f,
                1f, 0f, 1f, 0f,
                0f, 1f, 0f, 1f,
                1f, 1f, 1f, 1f,
            };
            var buffer = ByteBuffer.AllocateDirect(quad.Length * 4).Order(ByteOrder.NativeOrder()!);
            var floats = buffer.AsFloatBuffer()!;
            floats.Put(quad);
            floats.Position(0);
            int[] handles = new int[1];
            GLES20.GlGenBuffers(1, handles, 0);
            _quad = handles[0];
            GLES20.GlBindBuffer(GLES20.GlArrayBuffer, _quad);
            GLES20.GlBufferData(GLES20.GlArrayBuffer, quad.Length * 4, buffer, GLES20.GlStaticDraw);

            string extensions = GLES20.GlGetString(GLES20.GlExtensions) ?? string.Empty;
            Log.Info(Tag, "GL_RENDERER=" + GLES20.GlGetString(GLES20.GlRenderer));
            Log.Info(Tag, "ASTC in extensions: " + extensions.Contains("texture_compression_astc"));

            _texture = UploadTexture();
        }

        public void OnSurfaceChanged(IGL10? gl, int width, int height)
        {
            GLES20.GlViewport(0, 0, width, height);
        }

        public void OnDrawFrame(IGL10? gl)
        {
            GLES20.GlClear(GLES20.GlColorBufferBit);
            if (_texture == 0 || _done)
                return;

            // Draw the texture 1:1 into the framebuffer's bottom-left corner so the
            // glReadPixels(0,0,_width,_height) below samples exactly the texture and
            // not a stretched full-screen view of it.
            GLES20.GlViewport(0, 0, _width, _height);

            GLES20.GlUseProgram(_program);
            GLES20.GlActiveTexture(GLES20.GlTexture0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, _texture);
            GLES20.GlUniform1i(_uTex, 0);
            GLES20.GlUniform2f(_uSize, _width, _height);

            GLES20.GlBindBuffer(GLES20.GlArrayBuffer, _quad);
            GLES20.GlEnableVertexAttribArray(_aPosition);
            GLES20.GlVertexAttribPointer(_aPosition, 2, GLES20.GlFloat, false, 16, 0);
            GLES20.GlEnableVertexAttribArray(_aTexCoord);
            GLES20.GlVertexAttribPointer(_aTexCoord, 2, GLES20.GlFloat, false, 16, 8);
            GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
            GLES20.GlFinish();

            _done = true;
            Capture();
            _activity.RunOnUiThread(() => _activity.Finish());
        }

        private int UploadTexture()
        {
            string dir = _activity.GetExternalFilesDir("gpu")!.AbsolutePath;
            string sourcePath = Path.Combine(dir, _source);
            if (!File.Exists(sourcePath))
            {
                Log.Error(Tag, "missing source: " + sourcePath);
                return 0;
            }

            (byte[] rgba, int width, int height) = ImageIo.LoadRgba(sourcePath);
            Log.Info(Tag, "loaded " + sourcePath + " " + width + "x" + height);

            const int MaxSide = 512;
            if (width > MaxSide || height > MaxSide)
            {
                double scale = Math.Min((double)MaxSide / width, (double)MaxSide / height);
                int smallWidth = Math.Max(4, (int)Math.Round(width * scale));
                int smallHeight = Math.Max(4, (int)Math.Round(height * scale));
                rgba = Downscale(rgba, width, height, smallWidth, smallHeight);
                width = smallWidth;
                height = smallHeight;
                Log.Info(Tag, "downscaled to " + width + "x" + height);
            }

            _blockWidth = BlockWidth(_block);
            _blockHeight = _blockWidth;
            _width = width;
            _height = height;

            int blocksX = (width + _blockWidth - 1) / _blockWidth;
            int blocksY = (height + _blockHeight - 1) / _blockHeight;
            _payload = new byte[blocksX * blocksY * 16];
            long start = Java.Lang.JavaSystem.CurrentTimeMillis();
            AstcEncoder.Encode(rgba, width, height, _blockWidth, _blockHeight, _payload, AstcQuality.Fast);
            Log.Info(Tag, "encoded " + (Java.Lang.JavaSystem.CurrentTimeMillis() - start) + " ms");
            _software = new byte[width * height * 4];
            AstcCodec.Decode(_payload, width, height, _blockWidth, _blockHeight, _software);

            int[] handles = new int[1];
            GLES20.GlGenTextures(1, handles, 0);
            int texture = handles[0];
            GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);

            var data = ByteBuffer.AllocateDirect(_payload.Length).Order(ByteOrder.NativeOrder()!);
            data.Put(_payload);
            data.Position(0);

            int internalFormat = _blockWidth switch
            {
                4 => 0x93B0,
                5 => 0x93B2,
                6 => 0x93B4,
                8 => 0x93B7,
                _ => 0x93B4,
            };

            int error = GLES20.GlGetError();
            GLES20.GlCompressedTexImage2D(GLES20.GlTexture2d, 0, internalFormat, _width, _height, 0, _payload.Length, data);
            error = GLES20.GlGetError();
            Log.Info(Tag, "upload " + _block + " " + _width + "x" + _height + " bytes=" + _payload.Length + " glError=" + error);
            return texture;
        }

        private static int BlockWidth(string block) => block switch
        {
            "4x4" => 4,
            "5x5" => 5,
            "8x8" => 8,
            _ => 6,
        };

        /// <summary>Box filter downscale, used so a phone can encode a test image quickly.</summary>
        private static byte[] Downscale(byte[] source, int width, int height, int targetWidth, int targetHeight)
        {
            byte[] result = new byte[targetWidth * targetHeight * 4];
            for (int y = 0; y < targetHeight; y++)
            {
                int y0 = (int)((long)y * height / targetHeight);
                int y1 = Math.Max(y0 + 1, (int)(((long)y + 1) * height / targetHeight));
                for (int x = 0; x < targetWidth; x++)
                {
                    int x0 = (int)((long)x * width / targetWidth);
                    int x1 = Math.Max(x0 + 1, (int)(((long)x + 1) * width / targetWidth));
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int sy = y0; sy < Math.Min(y1, height); sy++)
                    {
                        for (int sx = x0; sx < Math.Min(x1, width); sx++)
                        {
                            int offset = (sy * width + sx) * 4;
                            r += source[offset];
                            g += source[offset + 1];
                            b += source[offset + 2];
                            a += source[offset + 3];
                            n++;
                        }
                    }

                    n = Math.Max(1, n);
                    int target = (y * targetWidth + x) * 4;
                    result[target] = (byte)(r / n);
                    result[target + 1] = (byte)(g / n);
                    result[target + 2] = (byte)(b / n);
                    result[target + 3] = (byte)(a / n);
                }
            }

            return result;
        }

        private void Capture()
        {
            string dir = _activity.GetExternalFilesDir("gpu")!.AbsolutePath;
            byte[] pixels = new byte[_width * _height * 4];
            var buffer = ByteBuffer.AllocateDirect(pixels.Length).Order(ByteOrder.NativeOrder()!);
            GLES20.GlReadPixels(0, 0, _width, _height, GLES20.GlRgba, GLES20.GlUnsignedByte, buffer);
            buffer.Position(0);
            buffer.Get(pixels);

            // glReadPixels returns bottom-up rows.
            byte[] top = new byte[pixels.Length];
            int stride = _width * 4;
            for (int y = 0; y < _height; y++)
                System.Buffer.BlockCopy(pixels, (_height - 1 - y) * stride, top, y * stride, stride);

            ImageIo.SavePng(Path.Combine(dir, "gpu.png"), top, _width, _height, true);
            if (_software is { } software)
                ImageIo.SavePng(Path.Combine(dir, "soft.png"), software, _width, _height, true);

            var report = new StringBuilder();
            double sum = 0;
            int worst = 0;
            int worstX = 0;
            int worstY = 0;
            int badBlocks = 0;
            if (_software is { } reference)
            {
                for (int i = 0; i < Math.Min(top.Length, reference.Length); i += 4)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int delta = top[i + c] - reference[i + c];
                        sum += delta * delta;
                        int abs = Math.Abs(delta);
                        if (abs > worst)
                        {
                            worst = abs;
                            int pixel = i / 4;
                            worstX = pixel % _width;
                            worstY = pixel / _width;
                        }
                    }
                }

                int pixelCount = Math.Max(1, _width * _height);
                double mse = sum / (pixelCount * 3.0);
                double psnr = mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);

                // Count blocks whose GPU pixels differ from the managed decode by more
                // than a small tolerance; those are the ones hardware rejects.
                int blocksX = (_width + _blockWidth - 1) / _blockWidth;
                int blocksY = (_height + _blockHeight - 1) / _blockHeight;
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        bool bad = false;
                        for (int y = 0; y < _blockHeight && !bad; y++)
                        {
                            int py = by * _blockHeight + y;
                            if (py >= _height) break;
                            for (int x = 0; x < _blockWidth; x++)
                            {
                                int px = bx * _blockWidth + x;
                                if (px >= _width) break;
                                int o = (py * _width + px) * 4;
                                if (Math.Abs(top[o] - reference[o]) > 6 ||
                                    Math.Abs(top[o + 1] - reference[o + 1]) > 6 ||
                                    Math.Abs(top[o + 2] - reference[o + 2]) > 6)
                                {
                                    bad = true;
                                    break;
                                }
                            }
                        }

                        if (bad) badBlocks++;
                    }
                }

                report.AppendLine("block=" + _block);
                report.AppendLine("size=" + _width + "x" + _height);
                report.AppendLine("payload=" + _payload?.Length);
                report.AppendLine("psnr=" + psnr.ToString("0.00"));
                report.AppendLine("worst=" + worst + " at " + worstX + "," + worstY);
                report.AppendLine("bad_blocks=" + badBlocks + "/" + (blocksX * blocksY));
                Log.Info(Tag, "compare psnr=" + psnr.ToString("0.00") + " worst=" + worst + " badBlocks=" + badBlocks);
            }

            File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
            File.WriteAllText(Path.Combine(dir, "gl.txt"), (GLES20.GlGetString(GLES20.GlExtensions) ?? string.Empty) ?? string.Empty);
        }

        private static int BuildProgram()
        {
            const string vertex = """
                attribute vec2 aPos;
                attribute vec2 aUv;
                uniform vec2 uSize;
                varying vec2 vUv;
                void main() {
                    vUv = aUv;
                    vec2 clip = aPos * 2.0 - 1.0;
                    gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
                }
                """;
            const string fragment = """
                precision mediump float;
                uniform sampler2D uTex;
                varying vec2 vUv;
                void main() {
                    gl_FragColor = texture2D(uTex, vUv);
                }
                """;

            int vs = Compile(GLES20.GlVertexShader, vertex);
            int fs = Compile(GLES20.GlFragmentShader, fragment);
            int program = GLES20.GlCreateProgram();
            GLES20.GlAttachShader(program, vs);
            GLES20.GlAttachShader(program, fs);
            GLES20.GlLinkProgram(program);
            int[] status = new int[1];
            GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, status, 0);
            if (status[0] == 0)
                Log.Error(Tag, "link failed: " + GLES20.GlGetProgramInfoLog(program));
            return program;
        }

        private static int Compile(int type, string source)
        {
            int shader = GLES20.GlCreateShader(type);
            GLES20.GlShaderSource(shader, source);
            GLES20.GlCompileShader(shader);
            int[] status = new int[1];
            GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, status, 0);
            if (status[0] == 0)
                Log.Error(Tag, "compile failed: " + GLES20.GlGetShaderInfoLog(shader));
            return shader;
        }
    }
}
