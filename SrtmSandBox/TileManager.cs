using System.IO;
using System.Linq;

namespace SrtmSandBox
{
    public class TileManager
    {
        private readonly DirectoryInfo _directory;
        private readonly TileInfo[] _tiles;

        public TileManager(DirectoryInfo directory, TileInfo[] tiles)
        {
            _directory = directory;
            _tiles = tiles;
        }

        public short GetElevation(double latitude, double longitude)
        {
            var tile = _tiles.FirstOrDefault(t => t.Contains(latitude, longitude));
            if (tile == null)
            {
                return 0;
            }

            var elevationMap = TiffTools.GetElevationMap(_directory, tile);
            return tile.GetElevation(latitude, longitude, elevationMap);
        }
    }
}
