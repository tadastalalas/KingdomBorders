using System;
using System.IO;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using Path = System.IO.Path;

namespace KingdomBorders
{
    public static class ModLog
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Configs",
            "KingdomBorders.log"
        );

        public static void Clear()
        {
            try { File.Delete(LogPath); } catch { }
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }
    }

    public class DiagnosticBehavior : CampaignBehaviorBase
    {
        private bool _initialized;
        private int _ticksWaited;
        private GameEntity? _testEntity;

        public override void RegisterEvents()
        {
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
        }

        private void OnTick(float dt)
        {
            if (_initialized)
                return;

            _ticksWaited++;
            if (_ticksWaited < 60)
                return;

            _initialized = true;
            ModLog.Clear();
            ModLog.Log("=== Line Strip Diagnostic Start ===");

            try
            {
                // Step 1: Get scene
                var mapScene = Campaign.Current?.MapSceneWrapper as MapScene;
                if (mapScene == null)
                {
                    ModLog.Log("FAIL: Could not get MapScene");
                    return;
                }
                Scene scene = mapScene.Scene;
                if (scene == null)
                {
                    ModLog.Log("FAIL: Scene is null");
                    return;
                }
                ModLog.Log("Scene obtained");

                // Step 2: Find two towns
                Settlement? townA = null;
                Settlement? townB = null;
                foreach (var s in Settlement.All)
                {
                    if (!s.IsTown) continue;
                    if (townA == null)
                    {
                        townA = s;
                    }
                    else
                    {
                        townB = s;
                        break;
                    }
                }
                if (townA == null || townB == null)
                {
                    ModLog.Log("FAIL: Could not find two towns");
                    return;
                }

                Vec2 posA = townA.Position.ToVec2();
                Vec2 posB = townB.Position.ToVec2();
                ModLog.Log($"Town A: {townA.Name} at ({posA.x:F2}, {posA.y:F2})");
                ModLog.Log($"Town B: {townB.Name} at ({posB.x:F2}, {posB.y:F2})");

                // Step 3: Generate points along the line, sampling terrain height
                int segmentCount = 20;
                float heightOffset = 1.0f;
                float lineWidth = 1.5f;
                uint color = 0xFFFF0000; // Red

                var points3D = new Vec3[segmentCount + 1];
                for (int i = 0; i <= segmentCount; i++)
                {
                    float t = (float)i / segmentCount;
                    Vec2 p2D = posA + (posB - posA) * t;

                    float height = 0f;
                    var campaignPos = new CampaignVec2(p2D, false);
                    Campaign.Current.MapSceneWrapper.GetHeightAtPoint(in campaignPos, ref height);

                    points3D[i] = new Vec3(p2D.x, p2D.y, height + heightOffset);
                }
                ModLog.Log($"Generated {points3D.Length} 3D points along line");

                // Step 4: Build quad-strip mesh
                Material mat = Material.GetFromResource("vertex_color_mat");
                if (mat == null)
                {
                    ModLog.Log("FAIL: vertex_color_mat not found");
                    return;
                }

                Mesh mesh = Mesh.CreateMesh(editable: true);
                if (mesh == null)
                {
                    ModLog.Log("FAIL: Mesh.CreateMesh returned null");
                    return;
                }
                mesh.SetMaterial(mat);

                UIntPtr lockHandle = mesh.LockEditDataWrite();

                int triCount = 0;
                for (int i = 0; i < points3D.Length - 1; i++)
                {
                    Vec3 p0 = points3D[i];
                    Vec3 p1 = points3D[i + 1];

                    // Direction along the line
                    Vec3 forward = p1 - p0;
                    forward.Normalize();

                    // Perpendicular in the horizontal plane (cross with Up)
                    Vec3 right = Vec3.CrossProduct(forward, Vec3.Up);
                    right.Normalize();

                    Vec3 offset = right * (lineWidth / 2f);

                    Vec3 v0 = p0 - offset;
                    Vec3 v1 = p0 + offset;
                    Vec3 v2 = p1 + offset;
                    Vec3 v3 = p1 - offset;

                    float uvY0 = (float)i / (points3D.Length - 1);
                    float uvY1 = (float)(i + 1) / (points3D.Length - 1);

                    Vec2 uv0 = new Vec2(0f, uvY0);
                    Vec2 uv1 = new Vec2(1f, uvY0);
                    Vec2 uv2 = new Vec2(1f, uvY1);
                    Vec2 uv3 = new Vec2(0f, uvY1);

                    // Front faces
                    mesh.AddTriangle(v0, v1, v2, uv0, uv1, uv2, color, lockHandle);
                    mesh.AddTriangle(v0, v2, v3, uv0, uv2, uv3, color, lockHandle);

                    // Back faces
                    mesh.AddTriangle(v2, v1, v0, uv2, uv1, uv0, color, lockHandle);
                    mesh.AddTriangle(v3, v2, v0, uv3, uv2, uv0, color, lockHandle);

                    triCount += 4;
                }

                mesh.UnlockEditDataWrite(lockHandle);
                mesh.ComputeNormals();
                mesh.RecomputeBoundingBox();
                ModLog.Log($"Mesh built: {triCount} triangles, {mesh.GetFaceCount()} faces");

                // Step 5: Create entity
                _testEntity = GameEntity.CreateEmpty(scene, isModifiableFromEditor: false);
                if (_testEntity == null)
                {
                    ModLog.Log("FAIL: Entity creation failed");
                    return;
                }

                MatrixFrame identityFrame = MatrixFrame.Identity;
                _testEntity.SetGlobalFrame(in identityFrame);
                _testEntity.AddMesh(mesh);
                _testEntity.SetVisibilityExcludeParents(true);
                _testEntity.SetReadyToRender(true);

                Vec3 bbMin = mesh.GetBoundingBoxMin();
                Vec3 bbMax = mesh.GetBoundingBoxMax();
                ModLog.Log($"Mesh BB min: ({bbMin.x:F2}, {bbMin.y:F2}, {bbMin.z:F2})");
                ModLog.Log($"Mesh BB max: ({bbMax.x:F2}, {bbMax.y:F2}, {bbMax.z:F2})");
                ModLog.Log($"=== Done — Line from {townA.Name} to {townB.Name} ===");
            }
            catch (Exception ex)
            {
                ModLog.Log($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                ModLog.Log(ex.StackTrace ?? "No stack trace");
            }
        }
    }
}