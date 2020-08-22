using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;

namespace SrtmSandBox
{
    static class TiffTools
    {
        static TiffTools()
        {
            Tiff.SetErrorHandler(new DisableErrorHandler());
        }

        public static IEnumerable<TileInfo> GetDirectoryTiles(string directoryPath)
            => GetDirectoryTiles(new DirectoryInfo(directoryPath));

        public static TileInfo[] GetDirectoryTiles(DirectoryInfo directory)
        {
            var configFilePath = Path.Combine(directory.FullName, "tiles.json");
            if (!File.Exists(configFilePath))
            {
                var allTiles = directory.GetFiles("*.zip").AsParallel().Select(GetTileInfo).ToArray();
                var serializedTiles = JsonSerializer.Serialize(allTiles, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configFilePath, serializedTiles);

                return allTiles;
            }
            else
            {
                var fileContent = File.ReadAllText(configFilePath);
                return JsonSerializer.Deserialize<TileInfo[]>(fileContent);
            }
        }

        public static TileInfo GetTileInfo(string filePath)
            => GetTileInfo(new FileInfo(filePath));

        public static TileInfo GetTileInfo(FileInfo zipFile)
        {
            using var memoryStream = GetZippedTiffStream(zipFile);

            using var tiff = TiffFromStream(memoryStream);

            int imageWidth = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int imageHeight = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

            double[] modelTransformation = tiff.GetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG)[1].ToDoubleArray();
            double west = modelTransformation[3];
            double north = modelTransformation[4];

            FieldValue[] modelPixelScaleTag = tiff.GetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG);
            double[] modelPixelScale = modelPixelScaleTag[1].ToDoubleArray();
            var DW = modelPixelScale[0];
            var DH = modelPixelScale[1];

            return new TileInfo(north, west, DH * imageHeight, DW * imageWidth, imageWidth, imageHeight, zipFile.Name);
        }

        public static short[] GetElevationMap(DirectoryInfo directory, TileInfo tileInfo)
        {
            using var memoryStream = GetZippedTiffStream(new FileInfo(Path.Combine(directory.FullName, tileInfo.FileName)));
            using var tiff = TiffFromStream(memoryStream);

            int stripCount = tiff.NumberOfStrips();
            int stripSize = tiff.StripSize();
            var elevationMap = new short[tileInfo.Width * tileInfo.Height];
            byte[] buffer = new byte[stripSize];
            for (int stripIndex = 0; stripIndex < stripCount; stripIndex++)
            {
                tiff.ReadEncodedStrip(stripIndex, buffer, 0, -1);
                Buffer.BlockCopy(buffer, 0, elevationMap, stripIndex * stripSize, stripSize);
            }

            return elevationMap;
        }

        private static Tiff TiffFromStream(MemoryStream memoryStream)
        {
            memoryStream.Position = 0;
            return Tiff.ClientOpen("Tiff from zipstream", "r", memoryStream, new TiffStream());
        }

        private static MemoryStream GetZippedTiffStream(FileInfo zipFile)
        {
            using var zipArchive = ZipFile.OpenRead(zipFile.FullName);
            var zippedTiff = zipArchive.Entries.Single(e => e.Name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase));
            using var zippedTiffStream = zippedTiff.Open();
            var memoryStream = new MemoryStream();
            zippedTiffStream.CopyTo(memoryStream);
            return memoryStream;
        }

        private class DisableErrorHandler : TiffErrorHandler
        {
            public override void WarningHandler(Tiff tif, string method, string format, params object[] args)
            {
                // do nothing, ie, do not write warnings to console
            }
            public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args)
            {
                // do nothing ie, do not write warnings to console
            }
        }
    }
}
