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

        public decimal LocalPrice { get; set; }

        public string Name { get; set; } = string.Empty;

        public long VolumePerDay { get; set; }

        public double JitaPrice { get; set; }

        public long CurrentStock { get; set; }
    }
}
