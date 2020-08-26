#nullable enable
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
            Tiff.SetTagExtender(TagExtender);
        }

        private static void TagExtender(Tiff tif)
        {
            TiffFieldInfo[] tiffFieldInfo =
            {
                new TiffFieldInfo(TiffTag.GEOTIFF_MODELPIXELSCALETAG, TiffFieldInfo.Variable, TiffFieldInfo.Variable,
                                  TiffType.DOUBLE, FieldBit.Custom, true, true, "scale"),
                new TiffFieldInfo(TiffTag.GEOTIFF_MODELTIEPOINTTAG, TiffFieldInfo.Variable, TiffFieldInfo.Variable,
                                  TiffType.DOUBLE, FieldBit.Custom, true, true, "tie"),
            };

            tif.MergeFieldInfo(tiffFieldInfo, tiffFieldInfo.Length);
        }

        public static IEnumerable<TileInfo> GetDirectoryTiles(string directoryPath)
            => GetDirectoryTiles(new DirectoryInfo(directoryPath));

        public static TileInfo[] GetDirectoryTiles(DirectoryInfo directory)
        {
            InitDirectory(directory);
            var fileContent = File.ReadAllText(GetConfigFilePath(directory));
            return JsonSerializer.Deserialize<TileInfo[]>(fileContent);
        }

        private static void InitDirectory(DirectoryInfo directory)
        {
            var configFilePath = GetConfigFilePath(directory);
            if (!File.Exists(configFilePath))
            {
                var allTiles = directory.GetFiles("*.zip").AsParallel().Select(GetTileInfo).ToArray();
                var serializedTiles = JsonSerializer.Serialize(allTiles, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configFilePath, serializedTiles);
            }
        }

        private static string GetConfigFilePath(DirectoryInfo directory) => Path.Combine(directory.FullName, "tiles.json");

        public static void SplitTiles(DirectoryInfo sourceDirectory, DirectoryInfo targetDirectory)
        {
            foreach (var tileInfo in GetDirectoryTiles(sourceDirectory))
            {
                var fileInfo = new FileInfo(Path.Combine(sourceDirectory.FullName, tileInfo.FileName!));
                SplitTile(fileInfo, 1.0, 1.0, targetDirectory);
            }

            InitDirectory(targetDirectory);
        }

        private static void SplitTile(FileInfo sourceFile, double latitudeSpan, double longitudeSpan, DirectoryInfo targetDirectory)
        {
            using var memoryStream = GetZippedTiffStream(sourceFile);
            using Tiff tiff = TiffFromStream(memoryStream);

            var tileInfo = GetTileInfo(tiff);

            var latitudeFactor = tileInfo.LatitudeSpan / latitudeSpan;
            if (latitudeFactor <= 1.0 || latitudeFactor % 1.0 != 0)
                throw new ArgumentException("latitudeFactor must be an integer", nameof(latitudeSpan));

            var longitudeFactor = tileInfo.LongitudeSpan / longitudeSpan;
            if (longitudeFactor <= 1.0 || latitudeFactor % 1.0 != 0)
                throw new ArgumentException("longitudeFactor must be an integer", nameof(longitudeSpan));

            var height = (int)(tileInfo.Height / latitudeFactor);
            var width = (int)(tileInfo.Width / longitudeFactor);
            var elevationMap = GetElevationMap(tiff);

            var subtileCreationData = new List<(double north, double west, short[] subTileData)>();
            for (double north = tileInfo.North; north > tileInfo.South; north -= latitudeSpan)
            {
                for (double west = tileInfo.West; west < tileInfo.East; west += longitudeSpan)
                {
                    var subTileData = GetSubTileData(tileInfo, north, west, width, height, elevationMap);
                    if (subTileData.Any(v => v != 0))
                    {
                        subtileCreationData.Add((north, west, subTileData));
                    }
                }
            }

            void CreateSubtileZip(double north, double west, short[] subTileData)
            {
                var subTileName = GetTileName(north, west, latitudeSpan, longitudeSpan);
                var subTileTiffStream = CreateSubTile(tiff, subTileName, north, west, width, height, subTileData);
                SaveTile(targetDirectory, subTileName, subTileTiffStream);
            }

            subtileCreationData.AsParallel()
                               .WithDegreeOfParallelism(4)
                               .ForAll(a => CreateSubtileZip(a.north, a.west, a.subTileData));
        }

        private static string GetTileName(double north, double west, double latitudeSpan, double longitudeSpan)
        {
            static string formatLatitude(double latitude) => $"{(latitude >= 0 ? "N" : "S")}{Math.Abs(latitude):00}";
            static string formatLongitude(double longitude) => $"{(longitude >= 0 ? "E" : "W")}{Math.Abs(longitude):00}";

            var south = north - latitudeSpan;
            var east = west + longitudeSpan;
            return $"{formatLatitude(north)}{formatLongitude(west)}-{formatLatitude(south)}{formatLongitude(east)}";
        }

        private static short[] GetSubTileData(TileInfo tileInfo, double north, double west, int width, int height, short[] elevationMap)
        {
            int yOffset = (int)(tileInfo.Height * (tileInfo.North - north) / tileInfo.LatitudeSpan);
            int xOffset = (int)(tileInfo.Width * (west - tileInfo.West) / tileInfo.LongitudeSpan);
            var result = new short[width * height];
            for (int row = 0; row < height; row++)
            {
                int sourceindex = xOffset + (yOffset + row) * tileInfo.Width;
                int destinationIndex = row * width;
                Array.Copy(elevationMap, sourceindex, result, destinationIndex, width);
            }

            return result;
        }

        private static MemoryStream CreateSubTile(Tiff sourceTiff, string subTileName, double north, double west, int width, int height, short[] data)
        {
            var tempTiffFilePath = Path.GetTempFileName();

            var subTileTiff = Tiff.Open(tempTiffFilePath, "w");
            CreateSubtileTiff(sourceTiff, subTileName, north, west, width, height, data, subTileTiff);
            subTileTiff.Close();

            var tiffStream = new MemoryStream();
            tiffStream.Write(File.ReadAllBytes(tempTiffFilePath));
            File.Delete(tempTiffFilePath);

            return tiffStream;
        }

        private static void CreateSubtileTiff(Tiff sourceTiff, string subTileName, double north, double west, int width, int height, short[] data, Tiff subTileTiff)
        {
            //LibTiff.net doesn't seem to be thread safe
            lock (sourceTiff)
            {
                TiffTag[] manualTags = { TiffTag.STRIPBYTECOUNTS, TiffTag.STRIPOFFSETS, TiffTag.TILEBYTECOUNTS, TiffTag.TILEOFFSETS, TiffTag.DOCUMENTNAME, TiffTag.IMAGEWIDTH, TiffTag.IMAGELENGTH, TiffTag.GEOTIFF_MODELTIEPOINTTAG, TiffTag.GEOTIFF_MODELPIXELSCALETAG };
                //Copy all fields value from source
                foreach (var tag in GetKnownTags(sourceTiff).Except(manualTags))
                {
                    subTileTiff.SetField(tag, sourceTiff.GetField(tag).Select(t => t.Value).ToArray());
                }

                subTileTiff.SetField(TiffTag.DOCUMENTNAME, subTileName + ".tif");
                subTileTiff.SetField(TiffTag.IMAGEWIDTH, width);
                subTileTiff.SetField(TiffTag.IMAGELENGTH, height);

                SetGeoTiffTags(sourceTiff, north, west, subTileTiff);
            }

            subTileTiff.CheckpointDirectory();

            int stripSize = width * sizeof(short);
            var rowData = new byte[stripSize];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(data, row * stripSize, rowData, 0, stripSize);
                subTileTiff.WriteEncodedStrip(row, rowData, stripSize);
            }
        }

        private static void SetGeoTiffTags(Tiff sourceTiff, double north, double west, Tiff subTileTiff)
        {
            var modelTiePointSource = sourceTiff.GetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG);
            var coordinatesParam = modelTiePointSource[1].ToDoubleArray();
            coordinatesParam[3] = west;
            coordinatesParam[4] = north;
            var modelTiePointTarget = modelTiePointSource.Select(v => v.Value).ToArray();
            modelTiePointTarget[1] = coordinatesParam;
            subTileTiff.SetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG, modelTiePointTarget);

            var modelPixelScaleSource = sourceTiff.GetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG);
            var modelPixelScaleTarget = modelPixelScaleSource.Select(v => v.Value).ToArray();
            subTileTiff.SetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG, modelPixelScaleTarget);
        }

        private static IEnumerable<TiffTag> GetKnownTags(Tiff tiff)
            => Enum.GetValues(typeof(TiffTag)).OfType<TiffTag>().Where(tag => tiff.GetField(tag) != null);

        private static void SaveTile(DirectoryInfo directory, string tileName, MemoryStream tiffStream)
        {
            var zipFilePath = Path.Combine(directory.FullName, tileName + ".zip");
            using var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create);
            var tiffZipEntry = zipArchive.CreateEntry(tileName + ".tif", CompressionLevel.Optimal);
            using var zipEntryStream = tiffZipEntry.Open();
            tiffStream.Position = 0;
            tiffStream.CopyTo(zipEntryStream);
        }

        public static TileInfo GetTileInfo(FileInfo zipFile)
        {
            using var memoryStream = GetZippedTiffStream(zipFile);
            using var tiff = TiffFromStream(memoryStream);

            TileInfo tileInfo = GetTileInfo(tiff);
            tileInfo.FileName = zipFile.Name;
            return tileInfo;
        }

        private static TileInfo GetTileInfo(Tiff tiff)
        {
            int imageWidth = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int imageHeight = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

            double[] modelTransformation = tiff.GetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG)[1].ToDoubleArray();
            double west = modelTransformation[3];
            double north = modelTransformation[4];

            FieldValue[] modelPixelScaleTag = tiff.GetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG);
            double[] modelPixelScale = modelPixelScaleTag[1].ToDoubleArray();
            var DW = modelPixelScale[0];
            var DH = modelPixelScale[1];

            return new TileInfo(north, west, DH * imageHeight, DW * imageWidth, imageWidth, imageHeight);
        }

        public static short[] GetElevationMap(DirectoryInfo directory, TileInfo tileInfo)
        {
            using var memoryStream = GetZippedTiffStream(new FileInfo(Path.Combine(directory.FullName, tileInfo.FileName!)));
            using var tiff = TiffFromStream(memoryStream);
            return GetElevationMap(tiff);
        }

        private static short[] GetElevationMap(Tiff tiff)
        {
            int stripCount = tiff.NumberOfStrips();
            int stripSize = tiff.StripSize();
            var elevationMap = new short[stripCount * stripSize];
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
