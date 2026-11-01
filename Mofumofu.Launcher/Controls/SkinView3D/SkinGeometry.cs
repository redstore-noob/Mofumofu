using System;
using System.Collections.Generic;
using System.Numerics;

namespace Mofumofu.Launcher.Controls.SkinView3D;

public enum SkinBodyPart
{
    Head,
    Body,
    RightArm,
    LeftArm,
    RightLeg,
    LeftLeg,
    Cape
}

public enum SkinLayer
{
    Base,
    Overlay
}

internal sealed class SkinMeshData
{
    public required SkinBodyPart Part { get; init; }
    public required SkinLayer Layer { get; init; }
    public required Vector3 Pivot { get; init; }
    public required Vector3 Offset { get; init; }
    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
}

internal static class SkinGeometry
{
    public const int FloatsPerVertex = 8;

    public static List<SkinMeshData> Build(SkinModel model, Func<SkinBodyPart, SkinLayer, bool> visible)
    {
        bool slim = model == SkinModel.Slim;
        float armWidth = slim ? 3f : 4f;
        var meshes = new List<SkinMeshData>(13);

        AddBodyPart(meshes, visible, SkinBodyPart.Head,
            8, 8, 8, 0, 0, 32, 0,
            new Vector3(0, 24, 0), new Vector3(0, 4, 0));
        AddBodyPart(meshes, visible, SkinBodyPart.Body,
            8, 12, 4, 16, 16, 16, 32,
            new Vector3(0, 12, 0), new Vector3(0, 6, 0));
        // 肩关节位于 ±5，手臂中心通过局部偏移定位，抬臂时绕肩旋转。
        AddBodyPart(meshes, visible, SkinBodyPart.RightArm,
            armWidth, 12, 4, 40, 16, 40, 32,
            new Vector3(-5, 22, 0), new Vector3(1 - armWidth / 2, -4, 0));
        AddBodyPart(meshes, visible, SkinBodyPart.LeftArm,
            armWidth, 12, 4, 32, 48, 48, 48,
            new Vector3(5, 22, 0), new Vector3(armWidth / 2 - 1, -4, 0));
        AddBodyPart(meshes, visible, SkinBodyPart.RightLeg,
            4, 12, 4, 0, 16, 0, 32,
            new Vector3(-2, 12, 0), new Vector3(0, -6, 0));
        AddBodyPart(meshes, visible, SkinBodyPart.LeftLeg,
            4, 12, 4, 16, 48, 0, 48,
            new Vector3(2, 12, 0), new Vector3(0, -6, 0));

        if (visible(SkinBodyPart.Cape, SkinLayer.Base))
        {
            meshes.Add(new SkinMeshData
            {
                Part = SkinBodyPart.Cape,
                Layer = SkinLayer.Base,
                Pivot = new Vector3(0, 24, -2.6f),
                Offset = new Vector3(0, -8, -0.5f),
                Vertices = CreateBox(10, 16, 1, 0, 0, 64, 32, 0, swapFrontBack: true),
                Indices = CreateIndices()
            });
        }

        return meshes;
    }

    public static SkinMeshData CreateNameTagQuad()
    {
        return new SkinMeshData
        {
            Part = SkinBodyPart.Head,
            Layer = SkinLayer.Overlay,
            Pivot = Vector3.Zero,
            Offset = Vector3.Zero,
            Vertices =
            [
                -0.5f,  0.5f, 0, 0, 0, 1, 0, 0,
                -0.5f, -0.5f, 0, 0, 0, 1, 0, 1,
                 0.5f, -0.5f, 0, 0, 0, 1, 1, 1,
                 0.5f,  0.5f, 0, 0, 0, 1, 1, 0
            ],
            Indices = [0, 1, 2, 0, 2, 3]
        };
    }

    private static void AddBodyPart(
        ICollection<SkinMeshData> meshes,
        Func<SkinBodyPart, SkinLayer, bool> visible,
        SkinBodyPart part, float width, float height, float depth,
        int u, int v, int overlayU, int overlayV,
        Vector3 pivot, Vector3 offset)
    {
        if (visible(part, SkinLayer.Base))
        {
            meshes.Add(new SkinMeshData
            {
                Part = part, Layer = SkinLayer.Base, Pivot = pivot, Offset = offset,
                Vertices = CreateBox(width, height, depth, u, v, 64, 64, 0),
                Indices = CreateIndices()
            });
        }

        if (visible(part, SkinLayer.Overlay))
        {
            meshes.Add(new SkinMeshData
            {
                Part = part, Layer = SkinLayer.Overlay, Pivot = pivot, Offset = offset,
                Vertices = CreateBox(width, height, depth, overlayU, overlayV, 64, 64,
                    part == SkinBodyPart.Head ? 0.5f : 0.25f),
                Indices = CreateIndices()
            });
        }
    }

    private static uint[] CreateIndices()
    {
        var indices = new uint[36];
        for (uint face = 0; face < 6; face++)
        {
            uint vertex = face * 4;
            int index = (int)face * 6;
            indices[index] = vertex;
            indices[index + 1] = vertex + 1;
            indices[index + 2] = vertex + 2;
            indices[index + 3] = vertex;
            indices[index + 4] = vertex + 2;
            indices[index + 5] = vertex + 3;
        }
        return indices;
    }

    private static float[] CreateBox(
        float width, float height, float depth,
        int u, int v, int textureWidth, int textureHeight, float inflate, bool swapFrontBack = false)
    {
        float w = width + inflate * 2;
        float h = height + inflate * 2;
        float d = depth + inflate * 2;
        float x = w / 2, y = h / 2, z = d / 2;
        var vertices = new List<float>(6 * 4 * FloatsPerVertex);
        // 披风位于人物背后，-Z 是向外展示的一面，使用展开图的主图区域。
        float frontU = u + depth;
        float backU = u + depth + width + depth;
        if (swapFrontBack) (frontU, backU) = (backU, frontU);

        // +Z 正面
        AddFace(vertices,
            new Vector3(-x, y, z), new Vector3(-x, -y, z),
            new Vector3(x, -y, z), new Vector3(x, y, z), new Vector3(0, 0, 1),
            frontU, v + depth, width, height, textureWidth, textureHeight);
        // -Z 背面
        AddFace(vertices,
            new Vector3(x, y, -z), new Vector3(x, -y, -z),
            new Vector3(-x, -y, -z), new Vector3(-x, y, -z), new Vector3(0, 0, -1),
            backU, v + depth, width, height, textureWidth, textureHeight);
        // +X 右侧
        AddFace(vertices,
            new Vector3(x, y, z), new Vector3(x, -y, z),
            new Vector3(x, -y, -z), new Vector3(x, y, -z), new Vector3(1, 0, 0),
            u + depth + width, v + depth, depth, height, textureWidth, textureHeight);
        // -X 左侧
        AddFace(vertices,
            new Vector3(-x, y, -z), new Vector3(-x, -y, -z),
            new Vector3(-x, -y, z), new Vector3(-x, y, z), new Vector3(-1, 0, 0),
            u, v + depth, depth, height, textureWidth, textureHeight);
        // +Y 顶部
        AddFace(vertices,
            new Vector3(-x, y, -z), new Vector3(-x, y, z),
            new Vector3(x, y, z), new Vector3(x, y, -z), new Vector3(0, 1, 0),
            u + depth, v, width, depth, textureWidth, textureHeight);
        // -Y 底部：展开图的 V 方向与顶部相反。
        AddFace(vertices,
            new Vector3(-x, -y, z), new Vector3(-x, -y, -z),
            new Vector3(x, -y, -z), new Vector3(x, -y, z), new Vector3(0, -1, 0),
            u + depth + width, v + depth, width, -depth, textureWidth, textureHeight);

        return vertices.ToArray();
    }

    private static void AddFace(
        ICollection<float> vertices,
        Vector3 topLeft, Vector3 bottomLeft, Vector3 bottomRight, Vector3 topRight,
        Vector3 normal, float u, float v, float width, float height,
        float textureWidth, float textureHeight)
    {
        float u0 = u / textureWidth;
        float v0 = v / textureHeight;
        float u1 = (u + width) / textureWidth;
        float v1 = (v + height) / textureHeight;
        AddVertex(vertices, topLeft, normal, u0, v0);
        AddVertex(vertices, bottomLeft, normal, u0, v1);
        AddVertex(vertices, bottomRight, normal, u1, v1);
        AddVertex(vertices, topRight, normal, u1, v0);
    }

    private static void AddVertex(ICollection<float> vertices, Vector3 position, Vector3 normal, float u, float v)
    {
        vertices.Add(position.X); vertices.Add(position.Y); vertices.Add(position.Z);
        vertices.Add(normal.X); vertices.Add(normal.Y); vertices.Add(normal.Z);
        vertices.Add(u); vertices.Add(v);
    }
}
