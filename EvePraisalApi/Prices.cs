using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.EvePraisalApi
{
    public class Prices
    {
        public PriceStatistics all { get; set; } = new PriceStatistics();

        public PriceStatistics buy { get; set; } = new PriceStatistics();

        public PriceStatistics sell { get; set; } = new PriceStatistics();

        public string strategy { get; set; } = string.Empty;

        public DateTime updated { get; set; }
    }
}
