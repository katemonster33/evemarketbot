using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.Adam4EveApi
{
    public class MarketPriceAndVolume
    {
        public string buy_price { get; set; } = string.Empty;

        public string sell_price { get; set; } = string.Empty;

        public string buy_volume { get; set; } = string.Empty;

        public string sell_volume { get; set; } = string.Empty;

        public string lupdate { get; set; } = string.Empty; 
    }
}
