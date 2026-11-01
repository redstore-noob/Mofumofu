using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Silk.NET.OpenGL;
using SilkPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace Mofumofu.Launcher.Controls.SkinView3D;

public partial class SkinView3D
{
    /// <summary>
    /// 着色器主体。Avalonia 在 Windows 上走 ANGLE（OpenGL ES 3.0），在 Linux/macOS 上通常是
    /// 桌面 OpenGL 3.3+；两边的版本指令与精度限定符不通用，所以这里不带 #version，
    /// 由 <see cref="CreateProgram"/> 按运行时实际的着色语言版本拼接。
    /// </summary>
    private const string VertexShaderBody = """
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aNormal;
        layout (location = 2) in vec2 aUv;
        uniform mat4 uModel;
        uniform mat4 uMvp;
        out vec3 vNormal;
        out vec2 vUv;
        void main()
        {
            vNormal = mat3(uModel) * aNormal;
            vUv = aUv;
            gl_Position = uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderBody = """
        uniform sampler2D uTexture;
        uniform bool uLit;
        in vec3 vNormal;
        in vec2 vUv;
        out vec4 FragColor;
        void main()
        {
            vec4 texel = texture(uTexture, vUv);
            if (texel.a < 0.004) discard;
            float diffuse = max(dot(normalize(vNormal), normalize(vec3(0.35, 0.8, 0.55))), 0.0);
            float light = uLit ? 0.68 + diffuse * 0.32 : 1.0;
            FragColor = vec4(texel.rgb * light, texel.a);
        }
        """;

    // 注意：GLSL 源码只能是纯 ASCII。着色器字符串里不要写中文注释，
    // 否则在按系统代码页编译时会把注释变成乱码字节，直接编译失败。

    private readonly List<GpuMesh> _gpuMeshes = new();
    private GL? _gl;
    private uint _program;
    private uint _skinTexture;
    private uint _capeTexture;
    private uint _nameTagTexture;
    private GpuMesh? _nameTagMesh;
    private int _modelLocation;
    private int _mvpLocation;
    private int _textureLocation;
    private int _litLocation;
    private bool _meshesDirty = true;
    private bool _skinTextureDirty = true;
    private bool _capeTextureDirty;
    private bool _nameTagTextureDirty;
    private bool _glFailed;
    private volatile float _renderTime;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        try
        {
            _gl = GL.GetApi(gl.GetProcAddress);
            _program = CreateProgram(_gl);
            _modelLocation = _gl.GetUniformLocation(_program, "uModel");
            _mvpLocation = _gl.GetUniformLocation(_program, "uMvp");
            _textureLocation = _gl.GetUniformLocation(_program, "uTexture");
            _litLocation = _gl.GetUniformLocation(_program, "uLit");
            _nameTagMesh = CreateMesh(_gl, SkinGeometry.CreateNameTagQuad());
            _glFailed = false;
        }
        catch (Exception error)
        {
            // 着色器/上下文出问题时不能让异常穿过渲染线程，否则整个启动器会被带走。
            _glFailed = true;
            _gl = null;
            Trace.TraceError("[SkinView3D] OpenGL 初始化失败：" + error);
            return;
        }

        _meshesDirty = true;
        _skinTextureDirty = true;
        _capeTextureDirty = true;
        _nameTagTextureDirty = true;
        // 初始化完成后必须主动要一帧，否则静态界面上这个控件会一直是空的。
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_glFailed) return;
        if (_gl is not { } api) return;

        try
        {
            RenderScene(api, fb);
        }
        catch (Exception error)
        {
            _glFailed = true;
            Trace.TraceError("[SkinView3D] 渲染失败：" + error);
        }
    }

    private void RenderScene(GL api, int fb)
    {
        UpdateTexture(api, ref _skinTexture, _skinData, ref _skinTextureDirty);
        UpdateTexture(api, ref _capeTexture, _capeData, ref _capeTextureDirty);
        UpdateTexture(api, ref _nameTagTexture, _nameTagData, ref _nameTagTextureDirty);
        if (_meshesDirty) RebuildMeshes(api);

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        // 与 OpenGlControlBase.GetPixelSize 的截断规则一致，避免小数布局尺寸下越过 FBO 边界。
        int pixelWidth = Math.Max(1, (int)(Bounds.Width * scaling));
        int pixelHeight = Math.Max(1, (int)(Bounds.Height * scaling));

        api.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        api.Viewport(0, 0, (uint)pixelWidth, (uint)pixelHeight);

        // GL 状态与 compositor 共用；清屏前恢复写掩码并取消裁剪。
        api.Disable(EnableCap.ScissorTest);
        api.ColorMask(true, true, true, true);
        api.DepthMask(true);
        Color background = Background;
        float alpha = background.A / 255f;
        api.ClearColor(background.R / 255f * alpha, background.G / 255f * alpha,
            background.B / 255f * alpha, alpha);
        api.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        api.Enable(EnableCap.DepthTest);
        api.DepthFunc(DepthFunction.Lequal);
        api.Disable(EnableCap.ScissorTest);
        api.Disable(EnableCap.RasterizerDiscard);
        api.Enable(EnableCap.Blend);
        // Avalonia 解码输出的是预乘 Alpha，RGB 不能再次乘 SrcAlpha。
        api.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        api.Disable(EnableCap.CullFace);

        api.UseProgram(_program);
        api.Uniform1(_textureLocation, 0);

        float yaw = MathF.PI / 180f * (float)CameraYaw;
        float pitch = MathF.PI / 180f * (float)CameraPitch;
        var target = new Vector3(0, 16, 0);
        float distance = 46f / (float)Zoom;
        var orbit = new Vector3(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));
        Vector3 cameraPosition = target + orbit * distance;
        Matrix4x4 view = Matrix4x4.CreateLookAt(cameraPosition, target, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 180f * (float)FieldOfView,
            (float)pixelWidth / pixelHeight, 1f, 256f);
        // System.Numerics 的投影深度为 [0,1]，OpenGL 要求 [-1,1]。
        // 同时避免过小的近平面：ANGLE 的 16 位深度缓冲否则无法分开相距 0.25 的皮肤层。
        projection.M33 = projection.M33 * 2 + 1;
        projection.M43 *= 2;
        SkinPose pose = SkinAnimationPlayer.Evaluate(Animation, _renderTime);

        // 相邻肢体的前后表面共面，外层膨胀后又有重叠；给肢体一个深度偏移，
        // 避免躯干/袖口和双腿之间出现随视角变化的条纹。
        foreach (GpuMesh mesh in _gpuMeshes)
        {
            if (mesh.Data.Part == SkinBodyPart.Cape && _capeTexture == 0) continue;
            uint texture = mesh.Data.Part == SkinBodyPart.Cape ? _capeTexture : _skinTexture;
            if (texture == 0) continue;

            Matrix4x4 model = CreatePartMatrix(mesh.Data, pose);
            float depthBias = mesh.Data.Part switch
            {
                SkinBodyPart.RightArm => 1f,
                SkinBodyPart.LeftArm => 2f,
                SkinBodyPart.RightLeg => 3f,
                SkinBodyPart.LeftLeg => 4f,
                _ => 0f
            };
            if (depthBias > 0)
            {
                api.PolygonOffset(1f, depthBias);
                api.Enable(EnableCap.PolygonOffsetFill);
            }
            else
                api.Disable(EnableCap.PolygonOffsetFill);
            DrawMesh(api, mesh, texture, model, view, projection, true);
        }

        api.Disable(EnableCap.PolygonOffsetFill);
        if (_nameTagData is not null && _nameTagMesh is not null && _nameTagTexture != 0)
            DrawNameTag(api, cameraPosition, target, view, projection, pose.RootPosition);

        api.BindVertexArray(0);
        api.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        api.BindTexture(TextureTarget.Texture2D, 0);
        api.UseProgram(0);
        api.Disable(EnableCap.DepthTest);
        api.Disable(EnableCap.Blend);
        api.Flush();

        TryCapture(api, fb, pixelWidth, pixelHeight);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is { } api)
        {
            foreach (GpuMesh mesh in _gpuMeshes) DeleteMesh(api, mesh);
            _gpuMeshes.Clear();
            if (_nameTagMesh is not null) DeleteMesh(api, _nameTagMesh);
            _nameTagMesh = null;
            if (_skinTexture != 0) api.DeleteTexture(_skinTexture);
            if (_capeTexture != 0) api.DeleteTexture(_capeTexture);
            if (_nameTagTexture != 0) api.DeleteTexture(_nameTagTexture);
            if (_program != 0) api.DeleteProgram(_program);
        }

        _skinTexture = _capeTexture = _nameTagTexture = _program = 0;
        _gl = null;
        _meshesDirty = true;
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlLost()
    {
        // 上下文已经没了，VAO/VBO 名字也随之失效，只需要丢掉引用等 OnOpenGlInit 重建。
        _gpuMeshes.Clear();
        _nameTagMesh = null;
        _skinTexture = _capeTexture = _nameTagTexture = _program = 0;
        _gl = null;
        _meshesDirty = true;
        base.OnOpenGlLost();
    }

    /// <summary>
    /// System.Numerics 用行向量约定：v' = v * A * B 表示"先施加 A，再施加 B"，
    /// 也就是越靠左的矩阵越先生效。所以一条真实的骨骼链必须从局部写到世界：
    /// 方块自身 → 沿 offset 挪到骨头位置 → 绕 pivot 旋转 → 躯干节点 → 整体位移/朝向。
    /// 反过来（把 root 写在最左边）等于先绕原点旋转原始顶点、再把各部件硬搬到位，
    /// 小角度看着没事，一弯腰/摆臂/平躺就会整条腿甩飞、手臂插进躯干。
    /// </summary>
    private static Matrix4x4 CreatePartMatrix(SkinMeshData mesh, SkinPose pose)
    {
        Vector3 partRotation = mesh.Part == SkinBodyPart.Body
            ? Vector3.Zero
            : pose.ForPart(mesh.Part);

        Matrix4x4 matrix =
            Matrix4x4.CreateTranslation(mesh.Offset) *
            CreateRotation(partRotation) *
            Matrix4x4.CreateTranslation(mesh.Pivot);

        if (mesh.Part is not (SkinBodyPart.LeftLeg or SkinBodyPart.RightLeg))
        {
            // 头、手臂和披风都挂在躯干节点上，弯腰时不会再各动各的。
            var bodyPivot = new Vector3(0, 12, 0);
            matrix *=
                Matrix4x4.CreateTranslation(-bodyPivot) *
                CreateRotation(pose.BodyRotation) *
                Matrix4x4.CreateTranslation(bodyPivot);
        }

        // 整体朝向（飞行/游泳会让人物平躺）绕模型中心转，绕脚底转会把整条人甩出画面。
        var spinPivot = new Vector3(0, 16, 0);
        return matrix *
               Matrix4x4.CreateTranslation(-spinPivot) *
               CreateRotation(pose.RootRotation) *
               Matrix4x4.CreateTranslation(spinPivot) *
               Matrix4x4.CreateTranslation(pose.RootPosition);
    }

    private static Matrix4x4 CreateRotation(Vector3 rotation) =>
        Matrix4x4.CreateRotationX(rotation.X) *
        Matrix4x4.CreateRotationY(rotation.Y) *
        Matrix4x4.CreateRotationZ(rotation.Z);

    private void RebuildMeshes(GL api)
    {
        foreach (GpuMesh mesh in _gpuMeshes) DeleteMesh(api, mesh);
        _gpuMeshes.Clear();
        foreach (SkinMeshData data in SkinGeometry.Build(_effectiveModel, IsPartVisible))
            _gpuMeshes.Add(CreateMesh(api, data));
        _gpuMeshes.Sort(static (left, right) =>
        {
            int layer = left.Data.Layer.CompareTo(right.Data.Layer);
            return layer != 0
                ? layer
                : (left.Data.Part == SkinBodyPart.Cape ? 0 : 1)
                    .CompareTo(right.Data.Part == SkinBodyPart.Cape ? 0 : 1);
        });
        _meshesDirty = false;
    }

    private unsafe void DrawMesh(
        GL api, GpuMesh mesh, uint texture,
        Matrix4x4 model, Matrix4x4 view, Matrix4x4 projection, bool lit)
    {
        api.ActiveTexture(TextureUnit.Texture0);
        api.BindTexture(TextureTarget.Texture2D, texture);
        SetMatrix(api, _modelLocation, model);
        SetMatrix(api, _mvpLocation, model * view * projection);
        api.Uniform1(_litLocation, lit ? 1 : 0);
        api.BindVertexArray(mesh.Vao);
        api.DrawElements(PrimitiveType.Triangles, (uint)mesh.IndexCount,
            DrawElementsType.UnsignedInt, (void*)0);
    }

    private void DrawNameTag(
        GL api, Vector3 cameraPosition, Vector3 target,
        Matrix4x4 view, Matrix4x4 projection, Vector3 rootPosition)
    {
        if (_nameTagData is null || _nameTagMesh is null) return;

        Vector3 normal = Vector3.Normalize(cameraPosition - target);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
        Vector3 up = Vector3.Normalize(Vector3.Cross(normal, right));
        float height = 3.2f;
        float width = height * _nameTagData.Width / _nameTagData.Height;
        Vector3 center = rootPosition + new Vector3(0, 35, 0);
        var model = new Matrix4x4(
            right.X * width, right.Y * width, right.Z * width, 0,
            up.X * height, up.Y * height, up.Z * height, 0,
            normal.X, normal.Y, normal.Z, 0,
            center.X, center.Y, center.Z, 1);
        DrawMesh(api, _nameTagMesh, _nameTagTexture, model, view, projection, false);
    }

    private static unsafe GpuMesh CreateMesh(GL api, SkinMeshData data)
    {
        uint vao = api.GenVertexArray();
        uint vbo = api.GenBuffer();
        uint ebo = api.GenBuffer();
        api.BindVertexArray(vao);

        fixed (float* vertexData = data.Vertices)
        {
            api.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            api.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(data.Vertices.Length * sizeof(float)),
                vertexData, BufferUsageARB.StaticDraw);
        }

        fixed (uint* indexData = data.Indices)
        {
            api.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            api.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(data.Indices.Length * sizeof(uint)),
                indexData, BufferUsageARB.StaticDraw);
        }

        const int stride = SkinGeometry.FloatsPerVertex * sizeof(float);
        api.EnableVertexAttribArray(0);
        api.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        api.EnableVertexAttribArray(1);
        api.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        api.EnableVertexAttribArray(2);
        api.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        api.BindVertexArray(0);
        return new GpuMesh { Data = data, Vao = vao, Vbo = vbo, Ebo = ebo, IndexCount = data.Indices.Length };
    }

    private static void DeleteMesh(GL api, GpuMesh mesh)
    {
        api.DeleteBuffer(mesh.Vbo);
        api.DeleteBuffer(mesh.Ebo);
        api.DeleteVertexArray(mesh.Vao);
    }

    private static void UpdateTexture(
        GL api, ref uint texture, SkinTextureData? data, ref bool dirty)
    {
        if (!dirty) return;
        dirty = false;
        if (data is null)
        {
            if (texture != 0) api.DeleteTexture(texture);
            texture = 0;
            return;
        }

        if (texture == 0) texture = api.GenTexture();
        api.BindTexture(TextureTarget.Texture2D, texture);
        api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        api.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            (uint)data.Width, (uint)data.Height, 0,
            SilkPixelFormat.Rgba, PixelType.UnsignedByte, data.Rgba.AsSpan());
    }

    private static uint CreateProgram(GL api)
    {
        bool es = IsEsContext(api);
        string version = es ? "#version 300 es\n" : "#version 330 core\n";
        string fragmentPrelude = es ? "precision highp float;\n" : string.Empty;

        uint vertex = CompileShader(api, ShaderType.VertexShader, version + VertexShaderBody);
        uint fragment = CompileShader(api, ShaderType.FragmentShader, version + fragmentPrelude + FragmentShaderBody);
        uint program = api.CreateProgram();
        api.AttachShader(program, vertex);
        api.AttachShader(program, fragment);
        api.LinkProgram(program);
        api.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
            throw new InvalidOperationException("SkinView3D 着色器链接失败：" + api.GetProgramInfoLog(program));
        api.DetachShader(program, vertex);
        api.DetachShader(program, fragment);
        api.DeleteShader(vertex);
        api.DeleteShader(fragment);
        return program;
    }

    private static bool IsEsContext(GL api)
    {
        try
        {
            string? slVersion = api.GetStringS(StringName.ShadingLanguageVersion);
            if (!string.IsNullOrEmpty(slVersion))
                return slVersion.Contains("ES", StringComparison.OrdinalIgnoreCase);

            string? version = api.GetStringS(StringName.Version);
            return version is not null && version.Contains("ES", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static uint CompileShader(GL api, ShaderType type, string source)
    {
        uint shader = api.CreateShader(type);
        api.ShaderSource(shader, source);
        api.CompileShader(shader);
        api.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
            throw new InvalidOperationException("SkinView3D 着色器编译失败：" + api.GetShaderInfoLog(shader) + "\n" + source);
        return shader;
    }

    private static unsafe void SetMatrix(GL api, int location, Matrix4x4 matrix)
    {
        // OpenGL 按列主序读取这段内存后，恰好得到 System.Numerics 行向量矩阵的转置，
        // 因而与 GLSL 的列向量乘法匹配，不需要再手动转置。
        Span<float> values = stackalloc float[16];
        values[0] = matrix.M11;
        values[1] = matrix.M12;
        values[2] = matrix.M13;
        values[3] = matrix.M14;
        values[4] = matrix.M21;
        values[5] = matrix.M22;
        values[6] = matrix.M23;
        values[7] = matrix.M24;
        values[8] = matrix.M31;
        values[9] = matrix.M32;
        values[10] = matrix.M33;
        values[11] = matrix.M34;
        values[12] = matrix.M41;
        values[13] = matrix.M42;
        values[14] = matrix.M43;
        values[15] = matrix.M44;
        api.UniformMatrix4(location, 1, false, ref values[0]);
    }

    private sealed class GpuMesh
    {
        public required SkinMeshData Data { get; init; }
        public required uint Vao { get; init; }
        public required uint Vbo { get; init; }
        public required uint Ebo { get; init; }
        public required int IndexCount { get; init; }
    }
}
