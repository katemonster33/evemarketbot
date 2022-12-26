using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.EvePraisalApi
{
    public class StructuredRequest
    {
        public string market_name { get; set; } = string.Empty;

        public bool persist { get; set; } = true;

        public List<ItemRequest> items { get; set; } = new List<ItemRequest>();
    }
}
