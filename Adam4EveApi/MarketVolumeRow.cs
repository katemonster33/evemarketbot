using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EveMarketBot.Adam4EveApi
{
    public class MarketVolumeRow
    {
        private MarketVolumeRow() { }

          
        public static MarketVolumeRow CreateFromCsv(string[] fields)
        {
            var newRow = new MarketVolumeRow()
            {
                TypeId = int.Parse(fields[0]),
                RegionId = int.Parse(fields[1]),
                Date = DateTime.Parse(fields[2]),
            };
            if (!string.IsNullOrEmpty(fields[3])) newRow.BuyVolumeLow = long.Parse(fields[3]);
            if (!string.IsNullOrEmpty(fields[4])) newRow.BuyVolumeAvg= long.Parse(fields[4]);
            if (!string.IsNullOrEmpty(fields[5])) newRow.BuyVolumeHigh = long.Parse(fields[5]);
            if (!string.IsNullOrEmpty(fields[6])) newRow.SellVolumeLow= long.Parse(fields[6]);
            if (!string.IsNullOrEmpty(fields[7])) newRow.SellVolumeAvg = long.Parse(fields[7]);
            if (!string.IsNullOrEmpty(fields[8])) newRow.SellVolumeHigh = long.Parse(fields[8]);
            return newRow;
        }
        public int TypeId { get; set; } = 0;

        public int RegionId { get; set; } = 0;

        public DateTime Date { get; set; } = DateTime.Now;

        public long BuyVolumeLow { get; set; } = 0;

        public long BuyVolumeHigh { get; set; } = 0;

        public long BuyVolumeAvg { get; set; } = 0;

        public long SellVolumeLow { get; set; } = 0;

        public long SellVolumeHigh { get; set; } = 0;

        public long SellVolumeAvg { get; set; } = 0;
    }
}
