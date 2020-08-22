using System;
using System.IO;
using System.Linq;

namespace SrtmSandBox
{
    class Program
    {
        const string dirPath = @"D:\SRTM";
        static void Main(string[] args)
        {
            double latitude;
            double longitude;
            if (args.Length < 2 || !double.TryParse(args[0], out latitude) || !double.TryParse(args[1], out longitude))
            {
#if DEBUG
                latitude = 45.832627;
                longitude = 6.864717;
#else
                Console.WriteLine("SrtmSandBox.exe [latitude] [longitude]");
                return;
#endif
            }

            var dir = new DirectoryInfo(dirPath);
            var allTiles = TiffTools.GetDirectoryTiles(dir).ToArray();
            var tileManager = new TileManager(dir, allTiles);
            Console.WriteLine(tileManager.GetElevation(latitude, longitude));
        }
    }
}
