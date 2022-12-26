using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.EvePraisalApi
{
    public class ItemRequest
    {
        public string name { get; set; } = string.Empty;

        public int type_id { get; set; }
    }
}
