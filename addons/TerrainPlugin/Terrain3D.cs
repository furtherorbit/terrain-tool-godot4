using System.Xml.Schema;
using System.IO.Compression;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
using Godot;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using TerrainEditor.Converters;

namespace TerrainEditor
{
    [Tool]
    public partial class Terrain3D : Node3D
    {
        //count of chunks per patch
        public const int PATCH_CHUNKS_AMOUNT = 16;

        //edges per chunk
        public const int PATCH_CHUNK_EDGES = 4;

        //units per vertex (also scale factor)
        public const float UNITS_PER_VERTEX = 100.0f;

        public void UpdatePosition()
        {
            foreach (var patch in terrainPatches)
                patch.UpdatePosition(this);
        }

        public void UpdateSettings()
        {
            foreach (var patch in terrainPatches)
                patch.UpdateSettings(this);
        }

        private void CacheNeighbors()
        {
            for (int pathIndex = 0; pathIndex < terrainPatches.Count(); pathIndex++)
            {
                var patch = terrainPatches[pathIndex];
                for (int chunkIndex = 0; chunkIndex < PATCH_CHUNKS_AMOUNT; chunkIndex++)
                {
                    patch.chunks[chunkIndex].CacheNeighbors(this, patch);
                }
            }
        }

        public  Terrain3D() : base(){
            SetNotifyTransform(true);
        }

        public TerrainPatch GetPatch(int x, int z)
        {
            for (int i = 0; i < terrainPatches.Count(); i++)
            {
                var patch = terrainPatches[i];
                if (patch.patchCoord.x == x && patch.patchCoord.y == z)
                {
                    return patch;
                }
            }

            return null;
        }

        public int GetPatchesCount()
        {
            return terrainPatches.Count;
        }

        public TerrainPatch GetPatch(int idx)
        {
            if (terrainPatches.Count >= idx)
                return terrainPatches[idx];
            else
                return null;
        }

        public static T[] FromByteArray<T>(byte[] source) where T : struct
        {
            T[] destination = new T[source.Length / Marshal.SizeOf(typeof(T))];
            GCHandle handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            try
            {
                IntPtr pointer = handle.AddrOfPinnedObject();
                Marshal.Copy(source, 0, pointer, source.Length);
                return destination;
            }
            finally
            {
                if (handle.IsAllocated)
                    handle.Free();
            }
        }
        /**
         * Creating a patch grid
         */
        public void CreatePatchGrid(int patchX, int patchY, int chunkSize)
        {
            ClearDraw();

            //delete prev patches
            terrainPatches.Clear();

            for (int x = 0; x < patchX; x++)
            {
                for (int y = 0; y < patchY; y++)
                {
                    createPatch(x, y, chunkSize);
                }
            }
        }

        /**
         * Creating a patch by given coords and chunksize
         */
        public void createPatch(Vector2i coord, int chunkSize)
        {
            createPatch(coord.x, coord.y, chunkSize);
        }

        /**
        * Creating a patch by given coords and chunksize
        */
        public void createPatch(int x, int y, int chunkSize)
        {
            float size = (chunkSize - 1) * Terrain3D.UNITS_PER_VERTEX * Terrain3D.PATCH_CHUNK_EDGES;

            var script = GD.Load<CSharpScript>("res://addons/TerrainPlugin/TerrainPatch.cs").New();
            var patch = script as TerrainPatch;

            patch.offset = new Vector3(x * size, 0.0f, y * size);
            patch.ResourceLocalToScene = true;
            patch.patchCoord = new Vector2i(x, y);

            terrainPatches.Add(patch);
            patch.Init(chunkSize);

            patch.createHeightmap();
            patch.createSplatmap(0);
            patch.createSplatmap(1);
        }

        /**
        * Load heightmap from given image
        */
        public Error loadHeightmapFromImage(Vector2i patchCoord, Image heightMapImage, HeightmapAlgo algo = HeightmapAlgo.R16, float heightmapScale = 5000)
        {
            var patch = GetPatch(patchCoord.x, patchCoord.y);
            if (patch == null || heightMapImage == null)
            {
                return Error.FileNotFound;
            }

            if (algo == HeightmapAlgo.R16)
            {
                if (heightMapImage.GetFormat() != Image.Format.L8)
                {
                    GD.PrintErr("The R16 Algorithm needs a 16bit Image with one channel (red).");
                    return Error.FileCorrupt;
                }
            }
            if (algo == HeightmapAlgo.RGB8_Full)
            {
                if (heightMapImage.GetFormat() != Image.Format.Rgb8 && heightMapImage.GetFormat() != Image.Format.Rgba8)
                {
                    GD.PrintErr("The RGB8 Algorithm needs a 8bit RGB or RGBA Image.");
                    return Error.FileCorrupt;
                }
            }
            if (algo == HeightmapAlgo.RGBA8_Half || algo == HeightmapAlgo.RGBA8_Normal)
            {
                if (heightMapImage.GetFormat() != Image.Format.Rgba8)
                {
                    GD.PrintErr("The RGB8 Algorithm needs a 8bit RGBA Image.");
                    return Error.FileCorrupt;
                }
            }

            float[] heightmapData = new float[patch.info.heightMapSize * patch.info.heightMapSize];

            for (int z = 0; z < patch.info.heightMapSize; z++)
            {
                for (int x = 0; x < patch.info.heightMapSize; x++)
                {
                    Color raw = heightMapImage.GetPixel(x, z);
                    if (algo == HeightmapAlgo.RGBA8_Half) //my tool 
                    {
                        float normalizedHeight = TerrainByteConverter.ReadNormalizedHeight16Bit(raw);
                        heightmapData[z * patch.info.heightMapSize + x] = normalizedHeight * heightmapScale;
                    }
                    else if (algo == HeightmapAlgo.RGB8_Full) //mapbox default
                    {
                        float height = -10000f + ((raw.r8 * 256f * 256f + raw.g8 * 256f + raw.b8) * 0.1f);
                        float normalizedHeight = height / 50; //reduce because 24bit of mapbox

                        heightmapData[z * patch.info.heightMapSize + x] = normalizedHeight * heightmapScale;
                    }
                    else if (algo == HeightmapAlgo.R16) //industrial default
                    {
                        heightmapData[z * patch.info.heightMapSize + x] = raw.r * heightmapScale;
                    }
                }
            }

            patch.createHeightmap(heightmapData);
            return Error.Ok;
        }

        /**
        * Load splatmap from given image
        */
        public Error loadSplatmapFromImage(Vector2i patchCoord, int idx, Image splatmapImage)
        {
            var patch = GetPatch(patchCoord.x, patchCoord.y);
            if (patch == null || splatmapImage == null)
            {
                return Error.FileNotFound;
            }

            Color[] splatmapData = new Color[patch.info.heightMapSize * patch.info.heightMapSize];

            for (int z = 0; z < patch.info.heightMapSize; z++)
            {
                for (int x = 0; x < patch.info.heightMapSize; x++)
                {
                    splatmapData[z * patch.info.heightMapSize + x] = splatmapImage.GetPixel(x, z);
                }
            }

            patch.createSplatmap(idx, splatmapData);
            return Error.Ok;
        }

        public Error Draw()
        {
            ClearDraw();

            if (terrainPatches.Count <= 0)
            {
                return Error.FileNotFound;
            }

            CacheNeighbors();

            int patchId = 0;
            foreach (var patch in terrainPatches)
            {
                var start = OS.GetTicksMsec();
                patch.Draw(this, terrainDefaultMaterial);

                GD.Print("[Patch][" + patchId + "] Draw time " + (OS.GetTicksMsec() - start) + " ms");
                patchId++;
            }

            UpdateGizmo();


            var kmX = getBounds().Size.x * 0.00001f;
            var kmY = getBounds().Size.z * 0.00001f;

            GD.Print("[Draw Size] " + kmX + "x" + kmY + "km");

            return Error.Ok;
        }

        public override void _Notification(int what)
        {
            if (what == NotificationExitWorld)
            {
                ClearDraw();
            }
            else if (what == NotificationEnterWorld)
            {
                Draw();
            }
            else if (what == NotificationTransformChanged)
            {
              //  UpdatePosition();
            }
            else if (what == NotificationVisibilityChanged)
            {
                UpdateSettings();
            }
        }

        protected void ClearDraw()
        {
            GD.Print("Clearing");

            foreach (var patch in terrainPatches)
            {
                patch.ClearDraw();
            }
        }

        public AABB getBounds()
        {
            var bounds = new AABB();
            foreach (var patch in terrainPatches)
            {
                bounds = bounds.Merge(patch.getBounds());
            }

            return bounds;
        }

    }
}
