using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot
{
    public class StockedItem
    {
        public int TypeId { get; set; }

        public int StockCount { get; set; }

        public decimal MinimumPrice { get; set; }

        public string Name { get; set; } = string.Empty;

        public int VolumePerDay { get; set; }

        public double JitaPrice { get; set; }
    }
}
