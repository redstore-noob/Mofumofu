using System;
using System.Numerics;

namespace Mofumofu.Launcher.Controls.SkinView3D;

public enum SkinAnimation
{
    None,
    Idle,
    Walking,
    Running,
    Flying,
    Swimming,
    Crouching,
    Hitting,
    Waving
}

internal sealed class SkinPose
{
    public Vector3 RootPosition;
    public Vector3 RootRotation;
    public Vector3 HeadRotation;
    public Vector3 BodyRotation;
    public Vector3 RightArmRotation;
    public Vector3 LeftArmRotation;
    public Vector3 RightLegRotation;
    public Vector3 LeftLegRotation;
    public Vector3 CapeRotation = new(0.18f, 0, 0);

    public Vector3 ForPart(SkinBodyPart part) => part switch
    {
        SkinBodyPart.Head => HeadRotation,
        SkinBodyPart.Body => BodyRotation,
        SkinBodyPart.RightArm => RightArmRotation,
        SkinBodyPart.LeftArm => LeftArmRotation,
        SkinBodyPart.RightLeg => RightLegRotation,
        SkinBodyPart.LeftLeg => LeftLegRotation,
        SkinBodyPart.Cape => CapeRotation,
        _ => Vector3.Zero
    };
}

internal static class SkinAnimationPlayer
{
    public static double Duration(SkinAnimation animation) => animation switch
    {
        SkinAnimation.Idle => 8,
        SkinAnimation.Walking => 1,
        SkinAnimation.Running => 0.8,
        SkinAnimation.Flying => 4,
        SkinAnimation.Swimming => 2,
        SkinAnimation.Crouching => 4,
        SkinAnimation.Hitting => 0.8,
        SkinAnimation.Waving => 2,
        _ => 1
    };

    public static SkinPose Evaluate(SkinAnimation animation, double time)
    {
        var pose = new SkinPose();
        float t = (float)time;
        float capeSway = 0.08f * MathF.Sin(t * 2.2f);
        pose.CapeRotation.X = 0.18f + capeSway;

        switch (animation)
        {
            case SkinAnimation.Idle:
                pose.HeadRotation = new Vector3(
                    0.06f * MathF.Sin(t * 0.9f),
                    0.12f * MathF.Sin(t * 0.55f), 0);
                pose.RightArmRotation.Z = -0.035f - 0.025f * MathF.Sin(t * 1.2f);
                pose.LeftArmRotation.Z = 0.035f + 0.025f * MathF.Sin(t * 1.2f);
                break;

            case SkinAnimation.Walking:
            {
                float cycle = t * 8f;
                float swing = MathF.Sin(cycle) * 0.72f;
                pose.RightArmRotation.X = swing;
                pose.LeftArmRotation.X = -swing;
                pose.RightLegRotation.X = -swing * 0.85f;
                pose.LeftLegRotation.X = swing * 0.85f;
                pose.RootPosition.Y = 0.12f * MathF.Abs(MathF.Cos(cycle));
                pose.CapeRotation.X = 0.3f + capeSway;
                break;
            }

            case SkinAnimation.Running:
            {
                float cycle = t * 12f;
                float swing = MathF.Sin(cycle);
                pose.BodyRotation.X = 0.18f;
                pose.HeadRotation.X = -0.12f;
                pose.RightArmRotation = new Vector3(swing * 1.05f, 0, -0.08f);
                pose.LeftArmRotation = new Vector3(-swing * 1.05f, 0, 0.08f);
                pose.RightLegRotation.X = -swing * 0.95f;
                pose.LeftLegRotation.X = swing * 0.95f;
                pose.RootPosition.Y = 0.28f * MathF.Abs(MathF.Cos(cycle));
                pose.CapeRotation.X = 0.62f + capeSway;
                break;
            }

            case SkinAnimation.Flying:
                pose.RootPosition = new Vector3(0, 4.5f, 0);
                pose.RootRotation = new Vector3(1.15f, 0, 0);
                pose.RightArmRotation = new Vector3(0.15f * MathF.Sin(t * 2), 0, -0.35f);
                pose.LeftArmRotation = new Vector3(-0.15f * MathF.Sin(t * 2), 0, 0.35f);
                pose.RightLegRotation.X = 0.18f + 0.12f * MathF.Sin(t * 2.5f);
                pose.LeftLegRotation.X = 0.18f - 0.12f * MathF.Sin(t * 2.5f);
                pose.CapeRotation.X = 0.85f + capeSway;
                break;

            case SkinAnimation.Swimming:
            {
                float cycle = t * 5.5f;
                pose.RootPosition = new Vector3(0, 4f, 0);
                pose.RootRotation = new Vector3(1.3f, 0, 0);
                pose.RightArmRotation = new Vector3(cycle, 0, -0.28f);
                pose.LeftArmRotation = new Vector3(cycle + MathF.PI, 0, 0.28f);
                pose.RightLegRotation.X = 0.35f * MathF.Sin(cycle * 1.4f);
                pose.LeftLegRotation.X = -0.35f * MathF.Sin(cycle * 1.4f);
                pose.CapeRotation.X = 0.75f + capeSway;
                break;
            }

            case SkinAnimation.Crouching:
                pose.RootPosition = new Vector3(0, -1.5f, -1.2f);
                pose.BodyRotation.X = 0.48f;
                pose.HeadRotation = new Vector3(-0.42f, 0.08f * MathF.Sin(t), 0);
                pose.RightArmRotation.X = -0.42f;
                pose.LeftArmRotation.X = -0.42f;
                pose.RightLegRotation.X = -0.12f;
                pose.LeftLegRotation.X = -0.12f;
                pose.CapeRotation.X = 0.52f + capeSway;
                break;

            case SkinAnimation.Hitting:
            {
                float swing = (MathF.Sin(t * 10f) + 1f) * 0.5f;
                pose.RightArmRotation = new Vector3(-2.2f + swing * 1.65f, 0, -0.18f);
                pose.LeftArmRotation.Z = 0.05f;
                pose.BodyRotation.Y = -0.12f * MathF.Sin(t * 10f);
                pose.CapeRotation.X = 0.28f + capeSway;
                break;
            }

            case SkinAnimation.Waving:
                pose.RightArmRotation = new Vector3(0, 0,
                    -MathF.PI + 0.25f - MathF.Sin(t * 8f) * 0.28f);
                pose.LeftArmRotation.Z = 0.04f;
                pose.HeadRotation = new Vector3(0, 0.12f, -0.08f);
                pose.RootPosition.Y = 0.08f * MathF.Sin(t * 4f);
                break;
        }

        return pose;
    }
}
