using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot
{
    public class StockList
    {
        public Dictionary<int, int> StockCountsByTypeId { get; set; } = new Dictionary<int, int>();

        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
