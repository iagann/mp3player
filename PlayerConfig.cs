using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace mp3player
{
    public class PlayerConfig
    {
        public double Volume { get; set; } = 50;
        public string LastTrackPath { get; set; } = "";
        public long LastPositionMs { get; set; } = 0;
        public List<string> History { get; set; } = new();
    }
}
