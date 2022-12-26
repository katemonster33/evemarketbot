using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.EveMarketerApi
{
    public class OrderStats
    {
        public int volume { get; set; }

        public double avg { get; set; }

        public double max { get; set; }

        public double min { get; set; }

        public double stddev { get; set; }

        public double median { get; set; }

        public double percentile { get; set; }
    }
}
