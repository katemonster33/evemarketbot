using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.Adam4EveApi
{
    public class MarketPriceRow
    {
        private MarketPriceRow() { }

        public static MarketPriceRow CreateFromCsv(string[] fields)
        {
            var newRow = new MarketPriceRow()
            {
                TypeId = int.Parse(fields[0]),
                RegionId = int.Parse(fields[1]),
                Date = DateTime.Parse(fields[2]),
            };
            if (!string.IsNullOrEmpty(fields[3])) newRow.BuyPriceLow = decimal.Parse(fields[3]);
            if (!string.IsNullOrEmpty(fields[4])) newRow.BuyPriceAvg = decimal.Parse(fields[4]);
            if (!string.IsNullOrEmpty(fields[5])) newRow.BuyPriceHigh = decimal.Parse(fields[5]);
            if (!string.IsNullOrEmpty(fields[6])) newRow.SellPriceLow = decimal.Parse(fields[6]);
            if (!string.IsNullOrEmpty(fields[7])) newRow.SellPriceAvg = decimal.Parse(fields[7]);
            if (!string.IsNullOrEmpty(fields[8])) newRow.SellPriceHigh = decimal.Parse(fields[8]);
            return newRow;
        }

        public int TypeId { get; set; } = 0;

        public int RegionId { get; set; } = 0;

        public DateTime Date { get; set; } = DateTime.Now;

        public decimal? BuyPriceLow { get; set; }

        public decimal? BuyPriceAvg { get; set; }

        public decimal? BuyPriceHigh { get; set; }

        public decimal? SellPriceLow { get; set; }

        public decimal? SellPriceAvg { get; set; }

        public decimal? SellPriceHigh { get; set; }
    }
}
