using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.EvePraisalApi
{
    public class ItemResponse
    {
        public Dictionary<string, string> meta { get; set; } = new Dictionary<string, string>();

        public string name { get; set; } = string.Empty;

        public Prices prices { get; set; } = new Prices();

        public int quantity { get; set; } = 0;

        public int typeID { get; set; } = 0;

        public string typeName { get; set; } = string.Empty;

        public double typeVolume { get; set; } = 0;
    }
}
